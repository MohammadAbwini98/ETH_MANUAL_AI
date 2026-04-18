using EthSignal.Domain.Models;
using Npgsql;

namespace EthSignal.Infrastructure.Db;

public sealed class RegimeRepository : IRegimeRepository
{
    private readonly string _connectionString;

    public RegimeRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task UpsertAsync(RegimeResult result, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO ""ETH"".regime_snapshots
                (symbol, candle_open_time_utc, regime, regime_score,
                 triggered_conditions, disqualifying_conditions, created_at_utc)
            VALUES (@s, @t, @r, @sc, @tc, @dc, NOW())
            ON CONFLICT (symbol, candle_open_time_utc)
            DO UPDATE SET
                regime = EXCLUDED.regime,
                regime_score = EXCLUDED.regime_score,
                triggered_conditions = EXCLUDED.triggered_conditions,
                disqualifying_conditions = EXCLUDED.disqualifying_conditions;", conn);

        cmd.Parameters.AddWithValue("s", result.Symbol);
        cmd.Parameters.AddWithValue("t", result.CandleOpenTimeUtc);
        cmd.Parameters.AddWithValue("r", result.Regime.ToString());
        cmd.Parameters.AddWithValue("sc", result.RegimeScore);
        cmd.Parameters.AddWithValue("tc", string.Join("|", result.TriggeredConditions));
        cmd.Parameters.AddWithValue("dc", string.Join("|", result.DisqualifyingConditions));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<RegimeResult?> GetLatestAsync(string symbol, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT candle_open_time_utc, regime, regime_score,
                   triggered_conditions, disqualifying_conditions
            FROM ""ETH"".regime_snapshots
            WHERE symbol = @s
            ORDER BY candle_open_time_utc DESC LIMIT 1;", conn);
        cmd.Parameters.AddWithValue("s", symbol);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return ReadResult(symbol, r);
    }

    public async Task<RegimeResult?> GetLatestBeforeAsync(
        string symbol,
        DateTimeOffset before,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT candle_open_time_utc, regime, regime_score,
                   triggered_conditions, disqualifying_conditions
            FROM ""ETH"".regime_snapshots
            WHERE symbol = @s AND candle_open_time_utc <= @before
            ORDER BY candle_open_time_utc DESC LIMIT 1;", conn);
        cmd.Parameters.AddWithValue("s", symbol);
        cmd.Parameters.AddWithValue("before", before);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return ReadResult(symbol, r);
    }

    public async Task<IReadOnlyList<RegimeResult>> GetHistoryAsync(
        string symbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT candle_open_time_utc, regime, regime_score,
                   triggered_conditions, disqualifying_conditions
            FROM ""ETH"".regime_snapshots
            WHERE symbol = @s AND candle_open_time_utc >= @from AND candle_open_time_utc < @to
            ORDER BY candle_open_time_utc;", conn);
        cmd.Parameters.AddWithValue("s", symbol);
        cmd.Parameters.AddWithValue("from", from);
        cmd.Parameters.AddWithValue("to", to);

        var results = new List<RegimeResult>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            results.Add(ReadResult(symbol, r));
        return results;
    }

    private static RegimeResult ReadResult(string symbol, NpgsqlDataReader r)
    {
        var triggered = r.GetString(3);
        var disqualifying = r.GetString(4);
        return new RegimeResult
        {
            Symbol = symbol,
            CandleOpenTimeUtc = r.GetFieldValue<DateTimeOffset>(0),
            Regime = Enum.Parse<Regime>(r.GetString(1)),
            RegimeScore = r.GetInt32(2),
            TriggeredConditions = string.IsNullOrEmpty(triggered) ? [] : triggered.Split('|'),
            DisqualifyingConditions = string.IsNullOrEmpty(disqualifying) ? [] : disqualifying.Split('|')
        };
    }
}
