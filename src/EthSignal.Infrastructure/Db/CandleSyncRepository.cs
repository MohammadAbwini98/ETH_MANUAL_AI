using Npgsql;

namespace EthSignal.Infrastructure.Db;

public sealed class CandleSyncRepository : ICandleSyncRepository
{
    private readonly string _connectionString;

    public CandleSyncRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task UpsertAsync(CandleSyncStatusRow row, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO ""ETH"".candle_sync_status
                (symbol, timeframe, status, sync_mode, is_table_empty,
                 requested_from_utc, requested_to_utc,
                 last_existing_candle_utc, last_synced_candle_utc,
                 offline_duration_sec, chunk_size_candles,
                 chunks_total, chunks_completed,
                 last_run_started_at_utc, last_run_finished_at_utc,
                 last_success_at_utc, last_error, updated_at_utc)
            VALUES
                (@sym, @tf, @st, @mode, @empty,
                 @rfrom, @rto,
                 @lex, @lsync,
                 @offsec, @csize,
                 @ctot, @cdone,
                 @rstart, @rfin,
                 @lsucc, @lerr, NOW())
            ON CONFLICT (symbol, timeframe) DO UPDATE SET
                status = EXCLUDED.status,
                sync_mode = EXCLUDED.sync_mode,
                is_table_empty = EXCLUDED.is_table_empty,
                requested_from_utc = EXCLUDED.requested_from_utc,
                requested_to_utc = EXCLUDED.requested_to_utc,
                last_existing_candle_utc = EXCLUDED.last_existing_candle_utc,
                last_synced_candle_utc = EXCLUDED.last_synced_candle_utc,
                offline_duration_sec = EXCLUDED.offline_duration_sec,
                chunk_size_candles = EXCLUDED.chunk_size_candles,
                chunks_total = EXCLUDED.chunks_total,
                chunks_completed = EXCLUDED.chunks_completed,
                last_run_started_at_utc = EXCLUDED.last_run_started_at_utc,
                last_run_finished_at_utc = EXCLUDED.last_run_finished_at_utc,
                last_success_at_utc = EXCLUDED.last_success_at_utc,
                last_error = EXCLUDED.last_error,
                updated_at_utc = NOW();", conn);

        cmd.Parameters.AddWithValue("sym", row.Symbol);
        cmd.Parameters.AddWithValue("tf", row.Timeframe);
        cmd.Parameters.AddWithValue("st", row.Status);
        cmd.Parameters.AddWithValue("mode", row.SyncMode);
        cmd.Parameters.AddWithValue("empty", row.IsTableEmpty);
        cmd.Parameters.AddWithValue("rfrom", (object?)row.RequestedFromUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("rto", (object?)row.RequestedToUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("lex", (object?)row.LastExistingCandleUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("lsync", (object?)row.LastSyncedCandleUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("offsec", row.OfflineDurationSec);
        cmd.Parameters.AddWithValue("csize", row.ChunkSizeCandles);
        cmd.Parameters.AddWithValue("ctot", row.ChunksTotal);
        cmd.Parameters.AddWithValue("cdone", row.ChunksCompleted);
        cmd.Parameters.AddWithValue("rstart", (object?)row.LastRunStartedAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("rfin", (object?)row.LastRunFinishedAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("lsucc", (object?)row.LastSuccessAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("lerr", (object?)row.LastError ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<CandleSyncStatusRow>> GetAllAsync(string symbol, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT symbol, timeframe, status, sync_mode, is_table_empty,
                   requested_from_utc, requested_to_utc,
                   last_existing_candle_utc, last_synced_candle_utc,
                   offline_duration_sec, chunk_size_candles,
                   chunks_total, chunks_completed,
                   last_run_started_at_utc, last_run_finished_at_utc,
                   last_success_at_utc, last_error
            FROM ""ETH"".candle_sync_status
            WHERE symbol = @sym
            ORDER BY timeframe;", conn);
        cmd.Parameters.AddWithValue("sym", symbol);

        var rows = new List<CandleSyncStatusRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(ReadRow(r));
        return rows;
    }

    public async Task<CandleSyncStatusRow?> GetAsync(string symbol, string timeframe, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT symbol, timeframe, status, sync_mode, is_table_empty,
                   requested_from_utc, requested_to_utc,
                   last_existing_candle_utc, last_synced_candle_utc,
                   offline_duration_sec, chunk_size_candles,
                   chunks_total, chunks_completed,
                   last_run_started_at_utc, last_run_finished_at_utc,
                   last_success_at_utc, last_error
            FROM ""ETH"".candle_sync_status
            WHERE symbol = @sym AND timeframe = @tf
            LIMIT 1;", conn);
        cmd.Parameters.AddWithValue("sym", symbol);
        cmd.Parameters.AddWithValue("tf", timeframe);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return ReadRow(r);
    }

    private static CandleSyncStatusRow ReadRow(NpgsqlDataReader r)
    {
        return new CandleSyncStatusRow(
            Symbol: r.GetString(0),
            Timeframe: r.GetString(1),
            Status: r.GetString(2),
            SyncMode: r.GetString(3),
            IsTableEmpty: r.GetBoolean(4),
            RequestedFromUtc: r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5),
            RequestedToUtc: r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6),
            LastExistingCandleUtc: r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7),
            LastSyncedCandleUtc: r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8),
            OfflineDurationSec: r.GetInt64(9),
            ChunkSizeCandles: r.GetInt32(10),
            ChunksTotal: r.GetInt32(11),
            ChunksCompleted: r.GetInt32(12),
            LastRunStartedAtUtc: r.IsDBNull(13) ? null : r.GetFieldValue<DateTimeOffset>(13),
            LastRunFinishedAtUtc: r.IsDBNull(14) ? null : r.GetFieldValue<DateTimeOffset>(14),
            LastSuccessAtUtc: r.IsDBNull(15) ? null : r.GetFieldValue<DateTimeOffset>(15),
            LastError: r.IsDBNull(16) ? null : r.GetString(16));
    }
}
