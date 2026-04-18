using EthSignal.Infrastructure.Db;
using FluentAssertions;
using Npgsql;

namespace EthSignal.Tests.Infrastructure;

[Collection("Database")]
public class CandleSyncRepositoryTests : IAsyncLifetime
{
    private const string ConnString = "Host=localhost;Port=5432;Database=ETH_BASE_TEST;Username=mohammadabwini";
    private readonly DbMigrator _migrator = new(ConnString);
    private readonly CandleSyncRepository _repo = new(ConnString);

    public async Task InitializeAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(ConnString);
        var dbName = builder.Database!;
        builder.Database = "postgres";

        await using var conn = new NpgsqlConnection(builder.ToString());
        await conn.OpenAsync();

        await using (var check = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @db", conn))
        {
            check.Parameters.AddWithValue("db", dbName);
            if (await check.ExecuteScalarAsync() == null)
            {
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", conn);
                await create.ExecuteNonQueryAsync();
            }
        }

        await _migrator.MigrateAsync();

        await using var truncateConn = new NpgsqlConnection(ConnString);
        await truncateConn.OpenAsync();
        await using var truncate = new NpgsqlCommand(@"TRUNCATE TABLE ""ETH"".candle_sync_status;", truncateConn);
        await truncate.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task UpsertAsync_PersistsAndUpdatesSyncState()
    {
        var startedAt = new DateTimeOffset(2026, 4, 10, 8, 0, 0, TimeSpan.Zero);
        var row = new CandleSyncStatusRow(
            Symbol: "ETHUSD",
            Timeframe: "1m",
            Status: "RUNNING",
            SyncMode: "OFFLINE_GAP_RECOVERY",
            IsTableEmpty: false,
            RequestedFromUtc: startedAt.AddMinutes(-10),
            RequestedToUtc: startedAt,
            LastExistingCandleUtc: startedAt.AddMinutes(-11),
            LastSyncedCandleUtc: startedAt.AddMinutes(-5),
            OfflineDurationSec: 600,
            ChunkSizeCandles: 100,
            ChunksTotal: 2,
            ChunksCompleted: 1,
            LastRunStartedAtUtc: startedAt,
            LastRunFinishedAtUtc: null,
            LastSuccessAtUtc: null,
            LastError: "429 rate limit");

        await _repo.UpsertAsync(row);

        await _repo.UpsertAsync(row with
        {
            Status = "READY",
            ChunksCompleted = 2,
            LastSyncedCandleUtc = startedAt.AddMinutes(-1),
            LastRunFinishedAtUtc = startedAt.AddMinutes(1),
            LastSuccessAtUtc = startedAt.AddMinutes(1),
            LastError = null
        });

        var stored = await _repo.GetAsync("ETHUSD", "1m");
        var all = await _repo.GetAllAsync("ETHUSD");

        stored.Should().NotBeNull();
        stored!.Status.Should().Be("READY");
        stored.ChunksCompleted.Should().Be(2);
        stored.LastSyncedCandleUtc.Should().Be(startedAt.AddMinutes(-1));
        stored.LastError.Should().BeNull();
        all.Should().ContainSingle(r => r.Timeframe == "1m" && r.Status == "READY");
    }
}
