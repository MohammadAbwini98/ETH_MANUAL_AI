using EthSignal.Domain.Models;
using Npgsql;
using NpgsqlTypes;

namespace EthSignal.Infrastructure.Db;

public sealed class CandleRepository : ICandleRepository
{
    private readonly string _connectionString;

    public CandleRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<int> BulkUpsertAsync(Timeframe tf, string symbol, IReadOnlyList<RichCandle> candles, CancellationToken ct = default)
    {
        if (candles.Count == 0) return 0;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Create temp table
        await using (var cmd = new NpgsqlCommand(
            $"DROP TABLE IF EXISTS _bulk; CREATE TEMP TABLE _bulk (LIKE \"ETH\".{tf.Table} INCLUDING DEFAULTS);", conn))
            await cmd.ExecuteNonQueryAsync(ct);

        // Binary COPY into temp table
        {
            await using var writer = await conn.BeginBinaryImportAsync(
                "COPY _bulk (symbol, datetime, bid_open, bid_high, bid_low, bid_close, " +
                "ask_open, ask_high, ask_low, ask_close, mid_open, mid_high, mid_low, mid_close, " +
                "volume, buyer_pct, seller_pct, is_closed, source_timestamp_utc, received_timestamp_utc) FROM STDIN (FORMAT BINARY)", ct);

            foreach (var c in candles)
            {
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(symbol, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(c.OpenTime, NpgsqlDbType.TimestampTz, ct);

                await writer.WriteAsync(c.BidOpen, NpgsqlDbType.Numeric, ct);
                await writer.WriteAsync(c.BidHigh, NpgsqlDbType.Numeric, ct);
                await writer.WriteAsync(c.BidLow, NpgsqlDbType.Numeric, ct);
                await writer.WriteAsync(c.BidClose, NpgsqlDbType.Numeric, ct);

                await writer.WriteAsync(c.AskOpen, NpgsqlDbType.Numeric, ct);
                await writer.WriteAsync(c.AskHigh, NpgsqlDbType.Numeric, ct);
                await writer.WriteAsync(c.AskLow, NpgsqlDbType.Numeric, ct);
                await writer.WriteAsync(c.AskClose, NpgsqlDbType.Numeric, ct);

                await writer.WriteAsync(c.MidOpen, NpgsqlDbType.Numeric, ct);
                await writer.WriteAsync(c.MidHigh, NpgsqlDbType.Numeric, ct);
                await writer.WriteAsync(c.MidLow, NpgsqlDbType.Numeric, ct);
                await writer.WriteAsync(c.MidClose, NpgsqlDbType.Numeric, ct);

                await writer.WriteAsync(c.Volume, NpgsqlDbType.Numeric, ct);
                await writer.WriteAsync(c.BuyerPct, NpgsqlDbType.Numeric, ct);
                await writer.WriteAsync(c.SellerPct, NpgsqlDbType.Numeric, ct);
                await writer.WriteAsync(c.IsClosed, NpgsqlDbType.Boolean, ct);

                if (c.SourceTimestampUtc.HasValue)
                    await writer.WriteAsync(c.SourceTimestampUtc.Value, NpgsqlDbType.TimestampTz, ct);
                else
                    await writer.WriteNullAsync(ct);

                await writer.WriteAsync(c.ReceivedTimestampUtc, NpgsqlDbType.TimestampTz, ct);
            }

            await writer.CompleteAsync(ct);
        }

        // Upsert from temp table
        await using var upsert = new NpgsqlCommand($@"
            INSERT INTO ""ETH"".{tf.Table}
                (symbol, datetime, bid_open, bid_high, bid_low, bid_close,
                 ask_open, ask_high, ask_low, ask_close, mid_open, mid_high, mid_low, mid_close,
                 volume, buyer_pct, seller_pct, is_closed, source_timestamp_utc, received_timestamp_utc,
                 created_at_utc, updated_at_utc)
            SELECT symbol, datetime, bid_open, bid_high, bid_low, bid_close,
                   ask_open, ask_high, ask_low, ask_close, mid_open, mid_high, mid_low, mid_close,
                   volume, buyer_pct, seller_pct, is_closed, source_timestamp_utc, received_timestamp_utc,
                   NOW(), NOW()
            FROM _bulk
            ON CONFLICT (symbol, datetime)
            DO UPDATE SET
                bid_open = EXCLUDED.bid_open, bid_high = EXCLUDED.bid_high,
                bid_low = EXCLUDED.bid_low, bid_close = EXCLUDED.bid_close,
                ask_open = EXCLUDED.ask_open, ask_high = EXCLUDED.ask_high,
                ask_low = EXCLUDED.ask_low, ask_close = EXCLUDED.ask_close,
                mid_open = EXCLUDED.mid_open, mid_high = EXCLUDED.mid_high,
                mid_low = EXCLUDED.mid_low, mid_close = EXCLUDED.mid_close,
                volume = EXCLUDED.volume, buyer_pct = EXCLUDED.buyer_pct,
                seller_pct = EXCLUDED.seller_pct, is_closed = EXCLUDED.is_closed,
                source_timestamp_utc = EXCLUDED.source_timestamp_utc,
                received_timestamp_utc = EXCLUDED.received_timestamp_utc,
                updated_at_utc = NOW();", conn);

        return await upsert.ExecuteNonQueryAsync(ct);
    }

    public async Task<RichCandle?> GetOpenCandleAsync(Timeframe tf, string symbol, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand($@"
            SELECT datetime,
                   bid_open, bid_high, bid_low, bid_close,
                   ask_open, ask_high, ask_low, ask_close,
                   volume, buyer_pct, seller_pct,
                   source_timestamp_utc, received_timestamp_utc,
                   is_closed
            FROM ""ETH"".{tf.Table}
            WHERE symbol = @s AND is_closed = false
            ORDER BY datetime DESC LIMIT 1;", conn);
        cmd.Parameters.AddWithValue("s", symbol);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;

        return ReadCandle(r);
    }

    public async Task<DateTimeOffset?> GetLatestClosedTimeAsync(Timeframe tf, string symbol, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand($@"
            SELECT MAX(datetime) FROM ""ETH"".{tf.Table}
            WHERE symbol = @s AND is_closed = true;", conn);
        cmd.Parameters.AddWithValue("s", symbol);

        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is null or DBNull) return null;
        // Npgsql may return DateTime from MAX() on timestamptz
        if (result is DateTime dt)
            return new DateTimeOffset(dt, TimeSpan.Zero);
        return (DateTimeOffset)result;
    }

    public async Task<long> CountCandlesAsync(Timeframe tf, string symbol, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand($@"
            SELECT COUNT(*) FROM ""ETH"".{tf.Table}
            WHERE symbol = @s;", conn);
        cmd.Parameters.AddWithValue("s", symbol);

        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is null or DBNull) return 0L;
        return Convert.ToInt64(result);
    }

    public async Task<DateTimeOffset?> GetEarliestClosedTimeAsync(Timeframe tf, string symbol, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand($@"
            SELECT MIN(datetime) FROM ""ETH"".{tf.Table}
            WHERE symbol = @s AND is_closed = true;", conn);
        cmd.Parameters.AddWithValue("s", symbol);

        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is null or DBNull) return null;
        if (result is DateTime dt)
            return new DateTimeOffset(dt, TimeSpan.Zero);
        return (DateTimeOffset)result;
    }

    public async Task UpsertOpenCandlesAsync(string symbol, Dictionary<Timeframe, RichCandle> candles, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        foreach (var (tf, c) in candles)
        {
            await using var cmd = new NpgsqlCommand($@"
                INSERT INTO ""ETH"".{tf.Table}
                    (symbol, datetime, bid_open, bid_high, bid_low, bid_close,
                     ask_open, ask_high, ask_low, ask_close,
                     mid_open, mid_high, mid_low, mid_close,
                     volume, buyer_pct, seller_pct, is_closed,
                     source_timestamp_utc, received_timestamp_utc, created_at_utc, updated_at_utc)
                VALUES (@s, @dt, @bo, @bh, @bl, @bc, @ao, @ah, @al, @ac,
                        @mo, @mh, @ml, @mc, @v, @bp, @sp, false, @sut, @rut, NOW(), NOW())
                ON CONFLICT (symbol, datetime)
                DO UPDATE SET
                    bid_high = @bh, bid_low = @bl, bid_close = @bc,
                    ask_high = @ah, ask_low = @al, ask_close = @ac,
                    mid_high = @mh, mid_low = @ml, mid_close = @mc,
                    volume = @v, buyer_pct = @bp, seller_pct = @sp, is_closed = false,
                    updated_at_utc = NOW()
                WHERE {tf.Table}.is_closed = false;", conn);

            cmd.Parameters.AddWithValue("s", symbol);
            cmd.Parameters.AddWithValue("dt", c.OpenTime);
            cmd.Parameters.AddWithValue("bo", c.BidOpen);
            cmd.Parameters.AddWithValue("bh", c.BidHigh);
            cmd.Parameters.AddWithValue("bl", c.BidLow);
            cmd.Parameters.AddWithValue("bc", c.BidClose);
            cmd.Parameters.AddWithValue("ao", c.AskOpen);
            cmd.Parameters.AddWithValue("ah", c.AskHigh);
            cmd.Parameters.AddWithValue("al", c.AskLow);
            cmd.Parameters.AddWithValue("ac", c.AskClose);
            cmd.Parameters.AddWithValue("mo", c.MidOpen);
            cmd.Parameters.AddWithValue("mh", c.MidHigh);
            cmd.Parameters.AddWithValue("ml", c.MidLow);
            cmd.Parameters.AddWithValue("mc", c.MidClose);
            cmd.Parameters.AddWithValue("v", c.Volume);
            cmd.Parameters.AddWithValue("bp", c.BuyerPct);
            cmd.Parameters.AddWithValue("sp", c.SellerPct);
            cmd.Parameters.AddWithValue("sut", (object?)c.SourceTimestampUtc ?? DBNull.Value);
            cmd.Parameters.AddWithValue("rut", c.ReceivedTimestampUtc);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task CloseCandlesAsync(string symbol, IReadOnlyList<(Timeframe Tf, RichCandle Final)> toClose, CancellationToken ct = default)
    {
        if (toClose.Count == 0) return;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        foreach (var (tf, c) in toClose)
        {
            await using var cmd = new NpgsqlCommand($@"
                UPDATE ""ETH"".{tf.Table}
                SET bid_high = @bh, bid_low = @bl, bid_close = @bc,
                    ask_high = @ah, ask_low = @al, ask_close = @ac,
                    mid_high = @mh, mid_low = @ml, mid_close = @mc,
                    volume = @v, buyer_pct = @bp, seller_pct = @sp,
                    is_closed = true, updated_at_utc = NOW()
                WHERE symbol = @s AND datetime = @dt;", conn);

            cmd.Parameters.AddWithValue("s", symbol);
            cmd.Parameters.AddWithValue("dt", c.OpenTime);
            cmd.Parameters.AddWithValue("bh", c.BidHigh);
            cmd.Parameters.AddWithValue("bl", c.BidLow);
            cmd.Parameters.AddWithValue("bc", c.BidClose);
            cmd.Parameters.AddWithValue("ah", c.AskHigh);
            cmd.Parameters.AddWithValue("al", c.AskLow);
            cmd.Parameters.AddWithValue("ac", c.AskClose);
            cmd.Parameters.AddWithValue("mh", c.MidHigh);
            cmd.Parameters.AddWithValue("ml", c.MidLow);
            cmd.Parameters.AddWithValue("mc", c.MidClose);
            cmd.Parameters.AddWithValue("v", c.Volume);
            cmd.Parameters.AddWithValue("bp", c.BuyerPct);
            cmd.Parameters.AddWithValue("sp", c.SellerPct);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task CloseAllOpenAsync(string symbol, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        foreach (var tf in Timeframe.All)
        {
            await using var cmd = new NpgsqlCommand($@"
                UPDATE ""ETH"".{tf.Table} SET is_closed = true, updated_at_utc = NOW()
                WHERE symbol = @s AND is_closed = false;", conn);
            cmd.Parameters.AddWithValue("s", symbol);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task CloseOpenCandlesBeforeAsync(string symbol, DateTimeOffset boundary, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        foreach (var tf in Timeframe.All)
        {
            await using var cmd = new NpgsqlCommand($@"
                UPDATE ""ETH"".{tf.Table} SET is_closed = true, updated_at_utc = NOW()
                WHERE symbol = @s AND is_closed = false
                  AND datetime + @dur <= @boundary;", conn);
            cmd.Parameters.AddWithValue("s", symbol);
            cmd.Parameters.AddWithValue("dur", tf.Duration);
            cmd.Parameters.AddWithValue("boundary", boundary);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<IReadOnlyList<DateTimeOffset>> GetCandleTimesAsync(
        Timeframe tf, string symbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand($@"
            SELECT datetime FROM ""ETH"".{tf.Table}
            WHERE symbol = @s AND datetime >= @from AND datetime < @to
            ORDER BY datetime;", conn);
        cmd.Parameters.AddWithValue("s", symbol);
        cmd.Parameters.AddWithValue("from", from);
        cmd.Parameters.AddWithValue("to", to);

        var result = new List<DateTimeOffset>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            result.Add(r.GetFieldValue<DateTimeOffset>(0));
        return result;
    }

    public async Task<IReadOnlyList<RichCandle>> GetClosedCandlesAsync(Timeframe tf, string symbol, int limit, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand($@"
            SELECT datetime,
                   bid_open, bid_high, bid_low, bid_close,
                   ask_open, ask_high, ask_low, ask_close,
                   volume, buyer_pct, seller_pct,
                   source_timestamp_utc, received_timestamp_utc,
                   is_closed
            FROM ""ETH"".{tf.Table}
            WHERE symbol = @s AND is_closed = true
            ORDER BY datetime DESC LIMIT @n;", conn);
        cmd.Parameters.AddWithValue("s", symbol);
        cmd.Parameters.AddWithValue("n", limit);

        var result = new List<RichCandle>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            result.Add(ReadCandle(r));

        result.Reverse(); // Oldest first
        return result;
    }

    public async Task<IReadOnlyList<RichCandle>> GetClosedCandlesInRangeAsync(Timeframe tf, string symbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand($@"
            SELECT datetime,
                   bid_open, bid_high, bid_low, bid_close,
                   ask_open, ask_high, ask_low, ask_close,
                   volume, buyer_pct, seller_pct,
                   source_timestamp_utc, received_timestamp_utc,
                   is_closed
            FROM ""ETH"".{tf.Table}
            WHERE symbol = @s AND is_closed = true AND datetime >= @from AND datetime < @to
            ORDER BY datetime ASC;", conn);
        cmd.Parameters.AddWithValue("s", symbol);
        cmd.Parameters.AddWithValue("from", from);
        cmd.Parameters.AddWithValue("to", to);

        var result = new List<RichCandle>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            result.Add(ReadCandle(r));
        return result;
    }

    public async Task<IReadOnlyList<RichCandle>> GetClosedCandlesAfterAsync(Timeframe tf, string symbol, DateTimeOffset after, int limit, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand($@"
            SELECT datetime,
                   bid_open, bid_high, bid_low, bid_close,
                   ask_open, ask_high, ask_low, ask_close,
                   volume, buyer_pct, seller_pct,
                   source_timestamp_utc, received_timestamp_utc,
                   is_closed
            FROM ""ETH"".{tf.Table}
            WHERE symbol = @s AND is_closed = true AND datetime >= @after
            ORDER BY datetime ASC LIMIT @n;", conn);
        cmd.Parameters.AddWithValue("s", symbol);
        cmd.Parameters.AddWithValue("after", after);
        cmd.Parameters.AddWithValue("n", limit);

        var result = new List<RichCandle>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            result.Add(ReadCandle(r));
        return result;
    }

    public async Task<int> RepairInvalidOhlcAsync(Timeframe tf, string symbol, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // U-05: Fix rows where high < max(open,close) or low > min(open,close)
        await using var cmd = new NpgsqlCommand($@"
            UPDATE ""ETH"".{tf.Table} SET
                bid_high = GREATEST(bid_high, bid_open, bid_close),
                bid_low  = LEAST(bid_low, bid_open, bid_close),
                ask_high = GREATEST(ask_high, ask_open, ask_close),
                ask_low  = LEAST(ask_low, ask_open, ask_close)
            WHERE symbol = @s AND is_closed = true
              AND (bid_high < GREATEST(bid_open, bid_close)
                OR bid_low > LEAST(bid_open, bid_close)
                OR ask_high < GREATEST(ask_open, ask_close)
                OR ask_low > LEAST(ask_open, ask_close));", conn);
        cmd.Parameters.AddWithValue("s", symbol);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static RichCandle ReadCandle(NpgsqlDataReader r)
    {
        var sourceTs = r.IsDBNull(12) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(12);
        // Column 14 = is_closed (added to all SELECT queries)
        bool isClosed = r.FieldCount > 14 ? r.GetBoolean(14) : true;
        return new RichCandle
        {
            OpenTime = r.GetFieldValue<DateTimeOffset>(0),
            BidOpen = r.GetDecimal(1),
            BidHigh = r.GetDecimal(2),
            BidLow = r.GetDecimal(3),
            BidClose = r.GetDecimal(4),
            AskOpen = r.GetDecimal(5),
            AskHigh = r.GetDecimal(6),
            AskLow = r.GetDecimal(7),
            AskClose = r.GetDecimal(8),
            Volume = r.GetDecimal(9),
            BuyerPct = r.GetDecimal(10),
            SellerPct = r.GetDecimal(11),
            SourceTimestampUtc = sourceTs,
            ReceivedTimestampUtc = r.GetFieldValue<DateTimeOffset>(13),
            IsClosed = isClosed
        };
    }
}
