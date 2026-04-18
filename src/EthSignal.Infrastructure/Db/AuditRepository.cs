using EthSignal.Domain.Models;
using Npgsql;

namespace EthSignal.Infrastructure.Db;

public sealed class AuditRepository : IAuditRepository
{
    private readonly string _connectionString;

    public AuditRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task InsertAuditAsync(IngestionAuditEntry entry, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO ""ETH"".ingestion_audit
                (operation, symbol, timeframe, period_from, period_to,
                 candles_fetched, candles_inserted, candles_updated, duplicates_skipped,
                 validation_errors, duration_ms, success, error_message, created_at_utc)
            VALUES
                (@op, @sym, @tf, @pf, @pt,
                 @cf, @ci, @cu, @ds,
                 @ve, @dm, @ok, @err, @cat);", conn);

        cmd.Parameters.AddWithValue("op", entry.Operation);
        cmd.Parameters.AddWithValue("sym", entry.Symbol);
        cmd.Parameters.AddWithValue("tf", entry.TimeframeName);
        cmd.Parameters.AddWithValue("pf", (object?)entry.PeriodFrom ?? DBNull.Value);
        cmd.Parameters.AddWithValue("pt", (object?)entry.PeriodTo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("cf", entry.CandlesFetched);
        cmd.Parameters.AddWithValue("ci", entry.CandlesInserted);
        cmd.Parameters.AddWithValue("cu", entry.CandlesUpdated);
        cmd.Parameters.AddWithValue("ds", entry.DuplicatesSkipped);
        cmd.Parameters.AddWithValue("ve", entry.ValidationErrors);
        cmd.Parameters.AddWithValue("dm", (long)entry.Duration.TotalMilliseconds);
        cmd.Parameters.AddWithValue("ok", entry.Success);
        cmd.Parameters.AddWithValue("err", (object?)entry.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("cat", entry.CreatedAtUtc);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertGapsAsync(IReadOnlyList<GapEvent> gaps, CancellationToken ct = default)
    {
        if (gaps.Count == 0) return;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        foreach (var gap in gaps)
        {
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO ""ETH"".gap_events
                    (symbol, timeframe, expected_time, actual_next_time, gap_duration_sec, gap_type, gap_source, detected_at_utc)
                VALUES (@sym, @tf, @et, @ant, @gds, @gt, @gs, @dat)
                ON CONFLICT (symbol, timeframe, expected_time) DO NOTHING;", conn);

            cmd.Parameters.AddWithValue("sym", gap.Symbol);
            cmd.Parameters.AddWithValue("tf", gap.TimeframeName);
            cmd.Parameters.AddWithValue("et", gap.ExpectedTime);
            cmd.Parameters.AddWithValue("ant", (object?)gap.ActualNextTime ?? DBNull.Value);
            cmd.Parameters.AddWithValue("gds", (int)gap.GapDuration.TotalSeconds);
            cmd.Parameters.AddWithValue("gt", gap.GapType);
            cmd.Parameters.AddWithValue("gs", gap.GapSource);
            cmd.Parameters.AddWithValue("dat", gap.DetectedAtUtc);

            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<bool> HasRecentUnresolvedGapsAsync(string symbol, int lookbackMinutes = 60, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // REQ-NS-002: LIVE gaps use detected_at_utc for recency; BACKFILL gaps use expected_time.
        // This prevents historical backfill gaps from blocking live processing.
        await using var cmd = new NpgsqlCommand(@"
            SELECT EXISTS(
                SELECT 1 FROM ""ETH"".gap_events
                WHERE symbol = @sym AND resolved = false
                  AND (
                    (gap_source = 'LIVE'     AND detected_at_utc > NOW() - @lookback * INTERVAL '1 minute')
                    OR
                    (gap_source = 'BACKFILL' AND expected_time   > NOW() - @lookback * INTERVAL '1 minute')
                  )
            );", conn);
        cmd.Parameters.AddWithValue("sym", symbol);
        cmd.Parameters.AddWithValue("lookback", lookbackMinutes);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is true;
    }

    /// <inheritdoc />
    public async Task<int> ResolveOldGapsAsync(string symbol, int maxAgeMinutes = 120, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            UPDATE ""ETH"".gap_events
            SET resolved = true, resolved_at_utc = NOW()
            WHERE symbol = @sym AND resolved = false
              AND (
                (gap_source = 'LIVE'     AND detected_at_utc < NOW() - @age * INTERVAL '1 minute')
                OR
                (gap_source = 'BACKFILL' AND expected_time   < NOW() - @age * INTERVAL '1 minute')
              )
            ;", conn);
        cmd.Parameters.AddWithValue("sym", symbol);
        cmd.Parameters.AddWithValue("age", maxAgeMinutes);

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async Task<GapDiagnostics> GetGapDiagnosticsAsync(string symbol, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT
                COUNT(*) FILTER (WHERE resolved = false
                    AND ((gap_source = 'LIVE' AND detected_at_utc > NOW() - INTERVAL '60 minutes')
                      OR (gap_source = 'BACKFILL' AND expected_time > NOW() - INTERVAL '60 minutes'))
                ) AS unresolved_recent,
                COUNT(*) FILTER (WHERE resolved = false) AS unresolved_total,
                MIN(expected_time) FILTER (WHERE resolved = false) AS oldest_expected,
                MAX(detected_at_utc) FILTER (WHERE resolved = false) AS newest_detected
            FROM ""ETH"".gap_events
            WHERE symbol = @sym;", conn);
        cmd.Parameters.AddWithValue("sym", symbol);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            return new GapDiagnostics(0, 0, null, null);

        return new GapDiagnostics(
            UnresolvedRecentCount: r.GetInt32(0),
            UnresolvedTotalCount: r.GetInt32(1),
            OldestUnresolvedExpectedTime: r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2),
            NewestDetectedAtUtc: r.IsDBNull(3) ? null : r.GetFieldValue<DateTimeOffset>(3));
    }
}
