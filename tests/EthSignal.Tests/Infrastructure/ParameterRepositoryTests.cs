using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using FluentAssertions;
using Npgsql;

namespace EthSignal.Tests.Infrastructure;

[Collection("Database")]
public sealed class ParameterRepositoryTests : IAsyncLifetime
{
    private const string ConnString = "Host=localhost;Port=5432;Database=ETH_BASE_TEST;Username=mohammadabwini";

    private readonly DbMigrator _migrator = new(ConnString);
    private readonly ParameterRepository _repository = new(ConnString);

    public async Task InitializeAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(ConnString);
        var dbName = builder.Database!;
        builder.Database = "postgres";

        await using var conn = new NpgsqlConnection(builder.ToString());
        await conn.OpenAsync();
        await using var check = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @db", conn);
        check.Parameters.AddWithValue("db", dbName);
        if (await check.ExecuteScalarAsync() == null)
        {
            await using var create = new NpgsqlCommand($@"CREATE DATABASE ""{dbName}""", conn);
            await create.ExecuteNonQueryAsync();
        }

        await _migrator.MigrateAsync();

        await using var truncateConn = new NpgsqlConnection(ConnString);
        await truncateConn.OpenAsync();
        await using (var truncate = new NpgsqlCommand(@"TRUNCATE TABLE ""ETH"".parameter_activation_history RESTART IDENTITY CASCADE;", truncateConn))
            await truncate.ExecuteNonQueryAsync();
        await using (var truncate = new NpgsqlCommand(@"TRUNCATE TABLE ""ETH"".strategy_parameter_sets RESTART IDENTITY CASCADE;", truncateConn))
            await truncate.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ActivateAsync_Retires_Only_Target_StrategyVersion()
    {
        var activeV31Id = await InsertSetAsync("v3.1", "hash-v31-active", ParameterSetStatus.Active);
        var candidateV31Id = await InsertSetAsync("v3.1", "hash-v31-candidate", ParameterSetStatus.Candidate);
        var activeV40Id = await InsertSetAsync("v4.0", "hash-v40-active", ParameterSetStatus.Active);

        await _repository.ActivateAsync(candidateV31Id, activeV31Id, "test", "promote candidate");

        var promoted = await _repository.GetByIdAsync(candidateV31Id);
        var retired = await _repository.GetByIdAsync(activeV31Id);
        var untouched = await _repository.GetByIdAsync(activeV40Id);

        promoted!.Status.Should().Be(ParameterSetStatus.Active);
        retired!.Status.Should().Be(ParameterSetStatus.Retired);
        untouched!.Status.Should().Be(ParameterSetStatus.Active);
    }

    [Fact]
    public async Task GetActiveAsync_Returns_Most_Recently_Activated_Set_For_StrategyVersion()
    {
        var olderId = await InsertSetAsync("v3.1", "hash-old", ParameterSetStatus.Active, activatedUtc: DateTimeOffset.UtcNow.AddMinutes(-30));
        var newerId = await InsertSetAsync("v3.1", "hash-new", ParameterSetStatus.Active, activatedUtc: DateTimeOffset.UtcNow.AddMinutes(-5));

        var active = await _repository.GetActiveAsync("v3.1");

        active.Should().NotBeNull();
        active!.Id.Should().Be(newerId);
        active.Id.Should().NotBe(olderId);
    }

    [Fact]
    public async Task InsertAsync_SelfHeals_RuntimeSchema_When_ParameterTables_AreMissing()
    {
        var connString = await CreateScratchDatabaseAsync("eth_param_repair");
        try
        {
            var repository = new ParameterRepository(connString);

            var id = await repository.InsertAsync(new StrategyParameterSet
            {
                StrategyVersion = "v3.1",
                ParameterHash = $"repair-{Guid.NewGuid():N}",
                Parameters = StrategyParameters.Default,
                Status = ParameterSetStatus.Candidate,
                CreatedUtc = DateTimeOffset.UtcNow,
                CreatedBy = "schema-repair-test"
            });

            var loaded = await repository.GetByIdAsync(id);
            loaded.Should().NotBeNull();
            loaded!.CreatedBy.Should().Be("schema-repair-test");
        }
        finally
        {
            await DropScratchDatabaseAsync(connString);
        }
    }

    private async Task<long> InsertSetAsync(string strategyVersion, string hash, ParameterSetStatus status, DateTimeOffset? activatedUtc = null)
    {
        var id = await _repository.InsertAsync(new StrategyParameterSet
        {
            StrategyVersion = strategyVersion,
            ParameterHash = hash,
            Parameters = StrategyParameters.Default with { StrategyVersion = strategyVersion },
            Status = status,
            CreatedUtc = DateTimeOffset.UtcNow,
            CreatedBy = "test"
        });

        if (activatedUtc.HasValue || status == ParameterSetStatus.Active)
        {
            await using var conn = new NpgsqlConnection(ConnString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                UPDATE ""ETH"".strategy_parameter_sets
                SET activated_utc = @activatedUtc
                WHERE id = @id;", conn);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("activatedUtc", activatedUtc ?? DateTimeOffset.UtcNow);
            await cmd.ExecuteNonQueryAsync();
        }

        return id;
    }

    private static async Task<string> CreateScratchDatabaseAsync(string prefix)
    {
        var dbName = $"{prefix}_{Guid.NewGuid().ToString("N")[..8]}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(ConnString) { Database = "postgres" };

        await using var conn = new NpgsqlConnection(adminBuilder.ToString());
        await conn.OpenAsync();
        await using var create = new NpgsqlCommand($@"CREATE DATABASE ""{dbName}""", conn);
        await create.ExecuteNonQueryAsync();

        return new NpgsqlConnectionStringBuilder(ConnString) { Database = dbName }.ToString();
    }

    private static async Task DropScratchDatabaseAsync(string connString)
    {
        NpgsqlConnection.ClearAllPools();
        var builder = new NpgsqlConnectionStringBuilder(connString);
        var dbName = builder.Database!;
        builder.Database = "postgres";

        await using var conn = new NpgsqlConnection(builder.ToString());
        await conn.OpenAsync();
        await using (var terminate = new NpgsqlCommand(@"
            SELECT pg_terminate_backend(pid)
            FROM pg_stat_activity
            WHERE datname = @db AND pid <> pg_backend_pid();", conn))
        {
            terminate.Parameters.AddWithValue("db", dbName);
            await terminate.ExecuteNonQueryAsync();
        }

        await using var drop = new NpgsqlCommand($@"DROP DATABASE IF EXISTS ""{dbName}""", conn);
        await drop.ExecuteNonQueryAsync();
    }
}
