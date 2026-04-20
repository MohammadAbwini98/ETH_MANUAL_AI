using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using FluentAssertions;
using Npgsql;

namespace EthSignal.Tests.Infrastructure;

[Collection("Database")]
public sealed class ExecutedTradeRepositoryTests : IAsyncLifetime
{
    private const string ConnString = "Host=localhost;Port=5432;Database=ETH_BASE_TEST;Username=mohammadabwini";
    private readonly DbMigrator _migrator = new(ConnString);
    private readonly ExecutedTradeRepository _repository = new(ConnString);

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
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", conn);
            await create.ExecuteNonQueryAsync();
        }

        await _migrator.MigrateAsync();

        await using var truncateConn = new NpgsqlConnection(ConnString);
        await truncateConn.OpenAsync();
        await using var truncate = new NpgsqlCommand(@"TRUNCATE TABLE ""ETH"".executed_trades RESTART IDENTITY CASCADE;", truncateConn);
        await truncate.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GetExecutedTradesAsync_AppliesCombinedFiltersCaseInsensitively()
    {
        var matchingTradeId = await _repository.InsertExecutedTradeAsync(CreateTrade(
            signalId: Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            sourceType: SignalExecutionSourceType.Generated,
            direction: SignalDirection.SELL,
            timeframe: "15m",
            status: ExecutedTradeStatus.Open,
            instrument: "ETHUSD",
            createdAtUtc: new DateTimeOffset(2026, 4, 10, 8, 0, 0, TimeSpan.Zero),
            openedAtUtc: new DateTimeOffset(2026, 4, 10, 10, 0, 0, TimeSpan.Zero)));

        await _repository.InsertExecutedTradeAsync(CreateTrade(
            signalId: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            sourceType: SignalExecutionSourceType.Recommended,
            direction: SignalDirection.BUY,
            timeframe: "5m",
            status: ExecutedTradeStatus.Loss,
            instrument: "BTCUSD",
            createdAtUtc: new DateTimeOffset(2026, 4, 10, 10, 30, 0, TimeSpan.Zero),
            openedAtUtc: new DateTimeOffset(2026, 4, 10, 10, 30, 0, TimeSpan.Zero)));

        var trades = await _repository.GetExecutedTradesAsync(new ExecutedTradeQuery
        {
            FromUtc = new DateTimeOffset(2026, 4, 10, 0, 0, 0, TimeSpan.Zero),
            ToUtc = new DateTimeOffset(2026, 4, 10, 23, 59, 59, TimeSpan.Zero),
            Instrument = "eth",
            Direction = SignalDirection.SELL,
            Timeframe = "15M",
            SourceType = SignalExecutionSourceType.Generated,
            Status = ExecutedTradeStatus.Open,
            Limit = 50
        });

        trades.Should().ContainSingle();
        trades[0].ExecutedTradeId.Should().Be(matchingTradeId);
    }

    [Fact]
    public async Task GetExecutedTradesAsync_FiltersByOpenedAt_WhenCreatedAtFallsOutsideRange()
    {
        var matchingTradeId = await _repository.InsertExecutedTradeAsync(CreateTrade(
            signalId: Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            sourceType: SignalExecutionSourceType.Blocked,
            direction: SignalDirection.BUY,
            timeframe: "5m",
            status: ExecutedTradeStatus.Open,
            instrument: "ETHUSD",
            createdAtUtc: new DateTimeOffset(2026, 4, 9, 23, 58, 0, TimeSpan.Zero),
            openedAtUtc: new DateTimeOffset(2026, 4, 10, 0, 2, 0, TimeSpan.Zero)));

        var trades = await _repository.GetExecutedTradesAsync(new ExecutedTradeQuery
        {
            FromUtc = new DateTimeOffset(2026, 4, 10, 0, 0, 0, TimeSpan.Zero),
            ToUtc = new DateTimeOffset(2026, 4, 10, 23, 59, 59, TimeSpan.Zero),
            Instrument = "ETHUSD",
            Limit = 50
        });

        trades.Should().ContainSingle();
        trades[0].ExecutedTradeId.Should().Be(matchingTradeId);
    }

    private static ExecutedTrade CreateTrade(
        Guid signalId,
        SignalExecutionSourceType sourceType,
        SignalDirection direction,
        string timeframe,
        ExecutedTradeStatus status,
        string instrument,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? openedAtUtc) => new()
    {
        SignalId = signalId,
        EvaluationId = Guid.NewGuid(),
        SourceType = sourceType,
        Symbol = instrument,
        Instrument = instrument,
        Timeframe = timeframe,
        Direction = direction,
        RecommendedEntryPrice = 2300m,
        ActualEntryPrice = 2300.5m,
        TpPrice = direction == SignalDirection.BUY ? 2310m : 2290m,
        SlPrice = direction == SignalDirection.BUY ? 2290m : 2310m,
        RequestedSize = 0.05m,
        ExecutedSize = 0.05m,
        DealReference = $"REF-{signalId:N}",
        DealId = $"DEAL-{signalId:N}",
        Status = status,
        AccountId = "demo-1",
        AccountName = "DEMOAI",
        IsDemo = true,
        AccountCurrency = "USD",
        Pnl = status == ExecutedTradeStatus.Loss ? -5m : 5m,
        CloseSource = status == ExecutedTradeStatus.Loss ? TradeCloseSource.StopLoss : null,
        OpenedAtUtc = openedAtUtc,
        ClosedAtUtc = status == ExecutedTradeStatus.Loss ? openedAtUtc?.AddMinutes(5) : null,
        CreatedAtUtc = createdAtUtc,
        UpdatedAtUtc = createdAtUtc.AddMinutes(1)
    };
}
