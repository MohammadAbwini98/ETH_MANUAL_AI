using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using FluentAssertions;
using Npgsql;

namespace EthSignal.Tests.Infrastructure;

[Collection("Database")]
public sealed class SignalRepositoryTests : IAsyncLifetime
{
    private const string ConnString = "Host=localhost;Port=5432;Database=ETH_BASE_TEST;Username=mohammadabwini";

    private readonly DbMigrator _migrator = new(ConnString);
    private readonly SignalRepository _repository = new(ConnString);

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
        await using (var truncate = new NpgsqlCommand(@"TRUNCATE TABLE ""ETH"".signal_outcomes RESTART IDENTITY CASCADE;", truncateConn))
            await truncate.ExecuteNonQueryAsync();
        await using (var truncate = new NpgsqlCommand(@"TRUNCATE TABLE ""ETH"".signals RESTART IDENTITY CASCADE;", truncateConn))
            await truncate.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GetLatestSignalAsync_Preserves_Evaluation_And_Exit_Metadata()
    {
        var signal = CreateSignal();
        await _repository.InsertSignalAsync(signal);

        var loaded = await _repository.GetLatestSignalAsync(signal.Symbol);

        loaded.Should().NotBeNull();
        loaded!.EvaluationId.Should().Be(signal.EvaluationId);
        loaded.MarketConditionClass.Should().Be(signal.MarketConditionClass);
        loaded.Tp1Price.Should().Be(signal.Tp1Price);
        loaded.Tp2Price.Should().Be(signal.Tp2Price);
        loaded.Tp3Price.Should().Be(signal.Tp3Price);
        loaded.RiskRewardRatio.Should().Be(signal.RiskRewardRatio);
        loaded.ExitModel.Should().Be(signal.ExitModel);
        loaded.ExitExplanation.Should().Be(signal.ExitExplanation);
    }

    [Fact]
    public async Task GetSignalHistoryWithOutcomesAsync_Preserves_Metadata_And_Links_Outcome()
    {
        var signal = CreateSignal();
        var outcome = new SignalOutcome
        {
            SignalId = signal.SignalId,
            EvaluatedAtUtc = DateTimeOffset.UtcNow,
            BarsObserved = 8,
            TpHit = true,
            SlHit = false,
            PartialWin = false,
            OutcomeLabel = OutcomeLabel.WIN,
            PnlR = 1.8m,
            MfePrice = signal.TpPrice,
            MaePrice = signal.SlPrice,
            MfeR = 2.1m,
            MaeR = -0.8m,
            ClosedAtUtc = DateTimeOffset.UtcNow
        };

        await _repository.InsertSignalAsync(signal);
        await _repository.InsertOutcomeAsync(outcome);

        var page = await _repository.GetSignalHistoryWithOutcomesAsync(signal.Symbol, limit: 20, offset: 0);

        page.Should().ContainSingle();
        page[0].Signal.EvaluationId.Should().Be(signal.EvaluationId);
        page[0].Signal.ExitModel.Should().Be(signal.ExitModel);
        page[0].Signal.ExitExplanation.Should().Be(signal.ExitExplanation);
        page[0].Outcome.Should().NotBeNull();
        page[0].Outcome!.OutcomeLabel.Should().Be(OutcomeLabel.WIN);
    }

    [Fact]
    public async Task InsertSignalAsync_SelfHeals_RuntimeSchema_When_Signals_Table_Is_Legacy()
    {
        var connString = await CreateScratchDatabaseAsync("eth_signal_repair");
        try
        {
            await CreateLegacySignalsSchemaAsync(connString);
            var repository = new SignalRepository(connString);
            var signal = CreateSignal();

            await repository.InsertSignalAsync(signal);

            var loaded = await repository.GetSignalByIdAsync(signal.SignalId);
            loaded.Should().NotBeNull();
            loaded!.EvaluationId.Should().Be(signal.EvaluationId);
            loaded.ExitModel.Should().Be(signal.ExitModel);
        }
        finally
        {
            await DropScratchDatabaseAsync(connString);
        }
    }

    private static SignalRecommendation CreateSignal() => new()
    {
        SignalId = Guid.NewGuid(),
        EvaluationId = Guid.NewGuid(),
        Symbol = "ETHUSD",
        Timeframe = "15m",
        SignalTimeUtc = new DateTimeOffset(2026, 4, 21, 12, 0, 0, TimeSpan.Zero),
        Direction = SignalDirection.BUY,
        EntryPrice = 2400m,
        TpPrice = 2424m,
        SlPrice = 2388m,
        RiskPercent = 0.5m,
        RiskUsd = 10m,
        ConfidenceScore = 79,
        Tp1Price = 2408m,
        Tp2Price = 2416m,
        Tp3Price = 2424m,
        RiskRewardRatio = 2.0m,
        ExitModel = "STRUCTURE_FULL",
        ExitExplanation = "Structure invalidation plus ATR buffer.",
        Regime = Regime.BULLISH,
        StrategyVersion = "v3.1",
        Reasons = ["signal-repository-test"],
        Status = SignalStatus.OPEN,
        MarketConditionClass = "NORMAL_MODERATE_LONDON_NORMAL_NORMAL"
    };

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

    private static async Task CreateLegacySignalsSchemaAsync(string connString)
    {
        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync();

        await using (var schema = new NpgsqlCommand(@"CREATE SCHEMA IF NOT EXISTS ""ETH"";", conn))
            await schema.ExecuteNonQueryAsync();

        await using var table = new NpgsqlCommand(@"
            CREATE TABLE IF NOT EXISTS ""ETH"".signals (
                signal_id           UUID        PRIMARY KEY,
                symbol              TEXT        NOT NULL,
                timeframe           TEXT        NOT NULL,
                signal_time_utc     TIMESTAMPTZ NOT NULL,
                direction           TEXT        NOT NULL,
                entry_price         NUMERIC     NOT NULL DEFAULT 0,
                tp_price            NUMERIC     NOT NULL DEFAULT 0,
                sl_price            NUMERIC     NOT NULL DEFAULT 0,
                risk_percent        NUMERIC     NOT NULL DEFAULT 0,
                risk_usd            NUMERIC     NOT NULL DEFAULT 0,
                confidence_score    INT         NOT NULL DEFAULT 0,
                regime              TEXT        NOT NULL DEFAULT 'NEUTRAL',
                strategy_version    TEXT        NOT NULL DEFAULT 'v1.0',
                reasons_json        JSONB       NOT NULL DEFAULT '[]'::jsonb,
                status              TEXT        NOT NULL DEFAULT 'OPEN',
                created_at_utc      TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );", conn);
        await table.ExecuteNonQueryAsync();
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
