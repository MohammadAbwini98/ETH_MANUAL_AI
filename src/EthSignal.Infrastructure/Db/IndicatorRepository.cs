using EthSignal.Domain.Models;
using Npgsql;
using NpgsqlTypes;

namespace EthSignal.Infrastructure.Db;

public sealed class IndicatorRepository : IIndicatorRepository
{
    private readonly string _connectionString;

    public IndicatorRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task BulkUpsertAsync(IReadOnlyList<IndicatorSnapshot> snapshots, CancellationToken ct = default)
    {
        if (snapshots.Count == 0) return;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Create temp table
        await using (var cmd = new NpgsqlCommand(
            "DROP TABLE IF EXISTS _ind_bulk; CREATE TEMP TABLE _ind_bulk (LIKE \"ETH\".indicator_snapshots INCLUDING DEFAULTS);", conn))
            await cmd.ExecuteNonQueryAsync(ct);

        // Binary COPY into temp
        {
            await using var writer = await conn.BeginBinaryImportAsync(
                "COPY _ind_bulk (symbol, timeframe, candle_open_time_utc, " +
                "ema20, ema50, rsi14, macd, macd_signal, macd_hist, " +
                "atr14, adx14, plus_di, minus_di, " +
                "volume_sma20, vwap, spread, close_mid, mid_high, mid_low, is_provisional) FROM STDIN (FORMAT BINARY)", ct);

            foreach (var s in snapshots)
            {
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(s.Symbol, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(s.Timeframe, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(s.CandleOpenTimeUtc, NpgsqlDbType.TimestampTz, ct);
                await writer.WriteAsync(s.Ema20, NpgsqlDbType.Numeric, ct);
                await writer.WriteAsync(s.Ema50, NpgsqlDbType.Numeric, ct);
                await writer.WriteAsync(s.Rsi14, NpgsqlDbType.Numeric, ct);
                await writer.WriteAsync(s.Macd, NpgsqlDbType.Numeric, ct);
                await writer.WriteAsync(s.MacdSignal, NpgsqlDbType.Numeric, ct);
                await writer.WriteAsync(s.MacdHist, NpgsqlDbType.Numeric, ct);
                await writer.WriteAsync(s.Atr14, NpgsqlDbType.Numeric, ct);
                await writer.WriteAsync(s.Adx14, NpgsqlDbType.Numeric, ct);
                await writer.WriteAsync(s.PlusDi, NpgsqlDbType.Numeric, ct);
                await writer.WriteAsync(s.MinusDi, NpgsqlDbType.Numeric, ct);
                await writer.WriteAsync(s.VolumeSma20, NpgsqlDbType.Numeric, ct);
                await writer.WriteAsync(s.Vwap, NpgsqlDbType.Numeric, ct);
                await writer.WriteAsync(s.Spread, NpgsqlDbType.Numeric, ct);
                await writer.WriteAsync(s.CloseMid, NpgsqlDbType.Numeric, ct);
                await writer.WriteAsync(s.MidHigh, NpgsqlDbType.Numeric, ct);
                await writer.WriteAsync(s.MidLow, NpgsqlDbType.Numeric, ct);
                await writer.WriteAsync(s.IsProvisional, NpgsqlDbType.Boolean, ct);
            }

            await writer.CompleteAsync(ct);
        }

        // Upsert from temp
        await using var upsert = new NpgsqlCommand(@"
            INSERT INTO ""ETH"".indicator_snapshots
                (symbol, timeframe, candle_open_time_utc,
                 ema20, ema50, rsi14, macd, macd_signal, macd_hist,
                 atr14, adx14, plus_di, minus_di,
                 volume_sma20, vwap, spread, close_mid, mid_high, mid_low, is_provisional,
                 created_at_utc)
            SELECT symbol, timeframe, candle_open_time_utc,
                   ema20, ema50, rsi14, macd, macd_signal, macd_hist,
                   atr14, adx14, plus_di, minus_di,
                   volume_sma20, vwap, spread, close_mid, mid_high, mid_low, is_provisional,
                   NOW()
            FROM _ind_bulk
            ON CONFLICT (symbol, timeframe, candle_open_time_utc)
            DO UPDATE SET
                ema20 = EXCLUDED.ema20, ema50 = EXCLUDED.ema50,
                rsi14 = EXCLUDED.rsi14,
                macd = EXCLUDED.macd, macd_signal = EXCLUDED.macd_signal, macd_hist = EXCLUDED.macd_hist,
                atr14 = EXCLUDED.atr14, adx14 = EXCLUDED.adx14,
                plus_di = EXCLUDED.plus_di, minus_di = EXCLUDED.minus_di,
                volume_sma20 = EXCLUDED.volume_sma20, vwap = EXCLUDED.vwap,
                spread = EXCLUDED.spread, close_mid = EXCLUDED.close_mid,
                mid_high = EXCLUDED.mid_high, mid_low = EXCLUDED.mid_low,
                is_provisional = EXCLUDED.is_provisional;", conn);
        await upsert.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<IndicatorSnapshot>> GetSnapshotsAsync(
        string symbol, string timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT candle_open_time_utc, ema20, ema50, rsi14,
                   macd, macd_signal, macd_hist,
                   atr14, adx14, plus_di, minus_di,
                   volume_sma20, vwap, spread, close_mid, mid_high, mid_low, is_provisional
            FROM ""ETH"".indicator_snapshots
            WHERE symbol = @s AND timeframe = @tf
              AND candle_open_time_utc >= @from AND candle_open_time_utc < @to
            ORDER BY candle_open_time_utc;", conn);
        cmd.Parameters.AddWithValue("s", symbol);
        cmd.Parameters.AddWithValue("tf", timeframe);
        cmd.Parameters.AddWithValue("from", from);
        cmd.Parameters.AddWithValue("to", to);

        var result = new List<IndicatorSnapshot>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            result.Add(new IndicatorSnapshot
            {
                Symbol = symbol,
                Timeframe = timeframe,
                CandleOpenTimeUtc = r.GetFieldValue<DateTimeOffset>(0),
                Ema20 = r.GetDecimal(1),
                Ema50 = r.GetDecimal(2),
                Rsi14 = r.GetDecimal(3),
                Macd = r.GetDecimal(4),
                MacdSignal = r.GetDecimal(5),
                MacdHist = r.GetDecimal(6),
                Atr14 = r.GetDecimal(7),
                Adx14 = r.GetDecimal(8),
                PlusDi = r.GetDecimal(9),
                MinusDi = r.GetDecimal(10),
                VolumeSma20 = r.GetDecimal(11),
                Vwap = r.GetDecimal(12),
                Spread = r.GetDecimal(13),
                CloseMid = r.GetDecimal(14),
                MidHigh = r.GetDecimal(15),
                MidLow = r.GetDecimal(16),
                IsProvisional = r.GetBoolean(17)
            });
        }
        return result;
    }

    public async Task<IndicatorSnapshot?> GetLatestAsync(string symbol, string timeframe, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT candle_open_time_utc, ema20, ema50, rsi14,
                   macd, macd_signal, macd_hist,
                   atr14, adx14, plus_di, minus_di,
                   volume_sma20, vwap, spread, close_mid, mid_high, mid_low, is_provisional
            FROM ""ETH"".indicator_snapshots
            WHERE symbol = @s AND timeframe = @tf AND is_provisional = false
            ORDER BY candle_open_time_utc DESC LIMIT 1;", conn);
        cmd.Parameters.AddWithValue("s", symbol);
        cmd.Parameters.AddWithValue("tf", timeframe);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;

        return new IndicatorSnapshot
        {
            Symbol = symbol,
            Timeframe = timeframe,
            CandleOpenTimeUtc = r.GetFieldValue<DateTimeOffset>(0),
            Ema20 = r.GetDecimal(1),
            Ema50 = r.GetDecimal(2),
            Rsi14 = r.GetDecimal(3),
            Macd = r.GetDecimal(4),
            MacdSignal = r.GetDecimal(5),
            MacdHist = r.GetDecimal(6),
            Atr14 = r.GetDecimal(7),
            Adx14 = r.GetDecimal(8),
            PlusDi = r.GetDecimal(9),
            MinusDi = r.GetDecimal(10),
            VolumeSma20 = r.GetDecimal(11),
            Vwap = r.GetDecimal(12),
            Spread = r.GetDecimal(13),
            CloseMid = r.GetDecimal(14),
            MidHigh = r.GetDecimal(15),
            MidLow = r.GetDecimal(16),
            IsProvisional = r.GetBoolean(17)
        };
    }
}
