using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using FluentAssertions;
using Npgsql;

namespace EthSignal.Tests.Infrastructure;

/// <summary>P1-T1 (fetch correctness), P1-T2 (duplicate prevention), P1-T3 (OHLC validity via DB round-trip).</summary>
[Collection("Database")]
public class CandleRepositoryTests : IAsyncLifetime
{
    private const string ConnString = "Host=localhost;Port=5432;Database=ETH_BASE_TEST;Username=mohammadabwini";
    private readonly DbMigrator _migrator = new(ConnString);
    private readonly CandleRepository _repo = new(ConnString);

    public async Task InitializeAsync()
    {
        // Ensure test DB exists
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
        await conn.CloseAsync();

        await _migrator.MigrateAsync();

        // Truncate all test tables
        await using var tConn = new NpgsqlConnection(ConnString);
        await tConn.OpenAsync();
        foreach (var tf in Timeframe.All)
        {
            await using var cmd = new NpgsqlCommand($@"TRUNCATE TABLE ""ETH"".{tf.Table};", tConn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static RichCandle MakeCandle(DateTimeOffset time, decimal basePrice = 2000m, decimal vol = 100m) => new()
    {
        OpenTime = time,
        BidOpen = basePrice, BidHigh = basePrice + 5, BidLow = basePrice - 5, BidClose = basePrice + 2,
        AskOpen = basePrice + 1, AskHigh = basePrice + 6, AskLow = basePrice - 4, AskClose = basePrice + 3,
        Volume = vol, ReceivedTimestampUtc = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task BulkUpsert_WritesAllCandles()
    {
        var t0 = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero);
        var candles = Enumerable.Range(0, 5)
            .Select(i => MakeCandle(t0.AddMinutes(i)))
            .ToList();

        var rows = await _repo.BulkUpsertAsync(Timeframe.M1, "ETHUSD", candles);
        rows.Should().Be(5);
    }

    [Fact]
    public async Task BulkUpsert_Duplicate_Upserts_Without_Error()
    {
        var t0 = new DateTimeOffset(2026, 3, 17, 11, 0, 0, TimeSpan.Zero);
        var candles = Enumerable.Range(0, 3)
            .Select(i => MakeCandle(t0.AddMinutes(i)))
            .ToList();

        await _repo.BulkUpsertAsync(Timeframe.M1, "ETHUSD", candles);
        // Insert same candles again — should upsert, not error
        var rows = await _repo.BulkUpsertAsync(Timeframe.M1, "ETHUSD", candles);
        rows.Should().Be(3);

        // Count should still be 3, not 6
        await using var conn = new NpgsqlConnection(ConnString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            @"SELECT COUNT(*) FROM ""ETH"".candles_1m WHERE symbol = 'ETHUSD' AND datetime >= @f AND datetime < @t", conn);
        cmd.Parameters.AddWithValue("f", t0);
        cmd.Parameters.AddWithValue("t", t0.AddMinutes(3));
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        count.Should().Be(3);
    }

    [Fact]
    public async Task IncrementalBackfill_OnlyFillsGaps()
    {
        var t0 = new DateTimeOffset(2026, 3, 17, 12, 0, 0, TimeSpan.Zero);

        // Insert hours 0-2 (3 candles)
        var first = Enumerable.Range(0, 3).Select(i => MakeCandle(t0.AddMinutes(i))).ToList();
        await _repo.BulkUpsertAsync(Timeframe.M1, "ETHUSD", first);

        // Insert hours 0-4 (5 candles) — overlaps first 3
        var second = Enumerable.Range(0, 5).Select(i => MakeCandle(t0.AddMinutes(i))).ToList();
        await _repo.BulkUpsertAsync(Timeframe.M1, "ETHUSD", second);

        // Should have exactly 5 rows
        await using var conn = new NpgsqlConnection(ConnString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            @"SELECT COUNT(*) FROM ""ETH"".candles_1m WHERE symbol = 'ETHUSD' AND datetime >= @f AND datetime < @t", conn);
        cmd.Parameters.AddWithValue("f", t0);
        cmd.Parameters.AddWithValue("t", t0.AddMinutes(5));
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        count.Should().Be(5);
    }

    [Fact]
    public async Task GetLatestClosedTime_ReturnsCorrectly()
    {
        var t0 = new DateTimeOffset(2026, 3, 17, 13, 0, 0, TimeSpan.Zero);
        var candles = Enumerable.Range(0, 3).Select(i => MakeCandle(t0.AddMinutes(i))).ToList();
        await _repo.BulkUpsertAsync(Timeframe.M1, "ETHUSD", candles);

        var latest = await _repo.GetLatestClosedTimeAsync(Timeframe.M1, "ETHUSD");
        latest.Should().NotBeNull();
        // Last candle in bulk insert is marked closed (all except the very last one)
        latest!.Value.Should().Be(t0.AddMinutes(2));
    }

    [Fact]
    public async Task CountCandles_And_GetEarliestClosedTime_ReturnCorrectValues()
    {
        var t0 = new DateTimeOffset(2026, 3, 17, 13, 30, 0, TimeSpan.Zero);
        var candles = Enumerable.Range(0, 4).Select(i => MakeCandle(t0.AddMinutes(i))).ToList();
        await _repo.BulkUpsertAsync(Timeframe.M1, "ETHUSD", candles);

        var count = await _repo.CountCandlesAsync(Timeframe.M1, "ETHUSD");
        var earliest = await _repo.GetEarliestClosedTimeAsync(Timeframe.M1, "ETHUSD");

        count.Should().Be(4);
        earliest.Should().Be(t0);
    }

    [Fact]
    public async Task CloseAllOpen_SetsIsClosedTrue()
    {
        var t0 = new DateTimeOffset(2026, 3, 17, 14, 0, 0, TimeSpan.Zero);
        var candle = MakeCandle(t0) with { IsClosed = false };
        await _repo.UpsertOpenCandlesAsync("ETHUSD", new Dictionary<Timeframe, RichCandle>
        {
            [Timeframe.M1] = candle
        });

        var open = await _repo.GetOpenCandleAsync(Timeframe.M1, "ETHUSD");
        open.Should().NotBeNull();

        await _repo.CloseAllOpenAsync("ETHUSD");

        open = await _repo.GetOpenCandleAsync(Timeframe.M1, "ETHUSD");
        open.Should().BeNull("all candles are now closed");
    }

    [Fact]
    public async Task GetCandleTimes_ReturnsCorrectTimestamps()
    {
        var t0 = new DateTimeOffset(2026, 3, 17, 15, 0, 0, TimeSpan.Zero);
        var candles = Enumerable.Range(0, 5).Select(i => MakeCandle(t0.AddMinutes(i))).ToList();
        await _repo.BulkUpsertAsync(Timeframe.M1, "ETHUSD", candles);

        var times = await _repo.GetCandleTimesAsync(Timeframe.M1, "ETHUSD", t0, t0.AddMinutes(5));
        times.Should().HaveCount(5);
        times[0].Should().Be(t0);
        times[4].Should().Be(t0.AddMinutes(4));
    }

    [Fact]
    public async Task Schema_Has_All_Required_Columns()
    {
        await using var conn = new NpgsqlConnection(ConnString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(@"
            SELECT column_name FROM information_schema.columns
            WHERE table_schema = 'ETH' AND table_name = 'candles_1m'
            ORDER BY ordinal_position;", conn);

        var columns = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            columns.Add(r.GetString(0));

        columns.Should().Contain("bid_open");
        columns.Should().Contain("bid_high");
        columns.Should().Contain("ask_close");
        columns.Should().Contain("mid_open");
        columns.Should().Contain("source_timestamp_utc");
        columns.Should().Contain("received_timestamp_utc");
        columns.Should().Contain("created_at_utc");
        columns.Should().Contain("updated_at_utc");
        columns.Should().Contain("is_closed");
        columns.Should().Contain("buyer_pct");
    }
}
