using System.Text.Json;
using Npgsql;

namespace EthSignal.Infrastructure.Db;

public sealed class MlDataDiagnosticsRepository : IMlDataDiagnosticsRepository
{
    private readonly string _connectionString;

    public MlDataDiagnosticsRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<IReadOnlyList<MlFeatureVersionStats>> GetFeatureVersionStatsAsync(
        string symbol,
        string timeframe,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT
                f.feature_version,
                COUNT(*)::INT AS total_snapshots,
                COUNT(*) FILTER (
                    WHERE EXISTS (
                        SELECT 1
                        FROM ""ETH"".signal_outcomes o
                        WHERE o.signal_id = f.signal_id
                          AND o.outcome_label IN ('WIN', 'LOSS')
                    )
                )::INT AS labeled_feature_snapshots,
                COUNT(*) FILTER (
                    WHERE f.signal_id IS NOT NULL
                      AND COALESCE(f.link_status, 'SIGNAL_LINKED') = 'SIGNAL_LINKED'
                      AND EXISTS (
                          SELECT 1
                          FROM ""ETH"".signal_outcomes o
                          WHERE o.signal_id = f.signal_id
                            AND o.outcome_label IN ('WIN', 'LOSS')
                      )
                )::INT AS trainable_feature_snapshots,
                MAX(f.created_at_utc)
            FROM ""ETH"".ml_feature_snapshots f
            WHERE f.symbol = @sym
              AND f.timeframe = @tf
            GROUP BY f.feature_version
            ORDER BY MAX(f.created_at_utc) DESC;", conn);
        cmd.Parameters.AddWithValue("sym", symbol);
        cmd.Parameters.AddWithValue("tf", timeframe);

        var results = new List<MlFeatureVersionStats>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new MlFeatureVersionStats(
                FeatureVersion: reader.GetString(0),
                TotalSnapshots: reader.GetInt32(1),
                LabeledFeatureSnapshots: reader.GetInt32(2),
                TrainableFeatureSnapshots: reader.GetInt32(3),
                LatestCreatedAtUtc: reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return results;
    }

    public async Task<MlOutcomeQualityRaw> GetOutcomeQualityAsync(
        string symbol,
        string timeframe,
        string featureVersion,
        int staleUnlinkedHours,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            WITH outcome_stats AS (
                SELECT
                    COUNT(o.signal_id)::INT AS total_outcomes,
                    COUNT(*) FILTER (WHERE o.outcome_label = 'WIN')::INT AS wins,
                    COUNT(*) FILTER (WHERE o.outcome_label = 'LOSS')::INT AS losses,
                    COUNT(*) FILTER (WHERE o.outcome_label = 'PENDING')::INT AS pending,
                    COUNT(*) FILTER (WHERE o.outcome_label = 'EXPIRED')::INT AS expired,
                    COUNT(*) FILTER (WHERE o.outcome_label = 'AMBIGUOUS')::INT AS ambiguous,
                    COUNT(*) FILTER (
                        WHERE (o.outcome_label = 'WIN' AND o.pnl_r <= 0)
                           OR (o.outcome_label = 'LOSS' AND o.pnl_r >= 0)
                    )::INT AS inconsistent_pnl_labels,
                    COUNT(*) FILTER (WHERE o.tp_hit AND o.sl_hit)::INT AS conflicting_tp_sl_hits,
                    COUNT(*) FILTER (
                        WHERE o.outcome_label IN ('WIN', 'LOSS', 'EXPIRED', 'AMBIGUOUS')
                          AND o.closed_at_utc IS NULL
                    )::INT AS closed_timestamp_missing
                FROM ""ETH"".signals s
                LEFT JOIN ""ETH"".signal_outcomes o ON o.signal_id = s.signal_id
                WHERE s.symbol = @sym
                  AND s.timeframe = @tf
            ),
            feature_stats AS (
                SELECT
                    COUNT(*)::INT AS total_feature_snapshots,
                    COUNT(*) FILTER (
                        WHERE f.signal_id IS NOT NULL
                           OR f.link_status = 'SIGNAL_LINKED'
                    )::INT AS linked_feature_snapshots,
                    COUNT(*) FILTER (
                        WHERE (f.signal_id IS NOT NULL OR f.link_status = 'SIGNAL_LINKED')
                          AND EXISTS (
                              SELECT 1
                              FROM ""ETH"".signal_outcomes o
                              WHERE o.signal_id = f.signal_id
                                AND o.outcome_label IN ('WIN', 'LOSS')
                          )
                    )::INT AS labeled_feature_snapshots,
                    -- Strict trainable count that matches ml/export_features.py
                    -- direct-linked query: signal_id IS NOT NULL AND link_status=SIGNAL_LINKED
                    -- AND outcome is WIN/LOSS. This is what training actually exports
                    -- when proximity fallback is off.
                    COUNT(*) FILTER (
                        WHERE f.signal_id IS NOT NULL
                          AND COALESCE(f.link_status, 'SIGNAL_LINKED') = 'SIGNAL_LINKED'
                          AND EXISTS (
                              SELECT 1
                              FROM ""ETH"".signal_outcomes o
                              WHERE o.signal_id = f.signal_id
                                AND o.outcome_label IN ('WIN', 'LOSS')
                          )
                    )::INT AS trainable_feature_snapshots,
                    COUNT(*) FILTER (
                        WHERE f.signal_id IS NULL
                          AND COALESCE(f.link_status, 'PENDING') = 'PENDING'
                    )::INT AS pending_link_snapshots,
                    COUNT(*) FILTER (
                        WHERE f.signal_id IS NULL
                          AND COALESCE(f.link_status, 'PENDING') = 'PENDING'
                          AND f.created_at_utc < NOW() - @stale_interval::interval
                    )::INT AS stale_pending_link_snapshots,
                    COUNT(*) FILTER (
                        WHERE f.signal_id IS NULL
                          AND COALESCE(f.link_status, 'PENDING') = 'NO_SIGNAL_EXPECTED'
                    )::INT AS expected_no_signal_snapshots,
                    COUNT(*) FILTER (
                        WHERE f.signal_id IS NULL
                          AND COALESCE(f.link_status, 'PENDING') = 'ML_FILTERED'
                    )::INT AS ml_filtered_snapshots,
                    COUNT(*) FILTER (
                        WHERE f.signal_id IS NULL
                          AND COALESCE(f.link_status, 'PENDING') = 'OPERATIONALLY_BLOCKED'
                    )::INT AS operationally_blocked_snapshots
                FROM ""ETH"".ml_feature_snapshots f
                WHERE f.symbol = @sym
                  AND f.timeframe = @tf
                  AND f.feature_version = @feature_version
            )
            SELECT
                o.total_outcomes,
                o.wins,
                o.losses,
                o.pending,
                o.expired,
                o.ambiguous,
                o.inconsistent_pnl_labels,
                o.conflicting_tp_sl_hits,
                o.closed_timestamp_missing,
                f.total_feature_snapshots,
                f.linked_feature_snapshots,
                f.labeled_feature_snapshots,
                f.pending_link_snapshots,
                f.stale_pending_link_snapshots,
                f.expected_no_signal_snapshots,
                f.ml_filtered_snapshots,
                f.operationally_blocked_snapshots,
                f.trainable_feature_snapshots
            FROM outcome_stats o
            CROSS JOIN feature_stats f;", conn);

        cmd.Parameters.AddWithValue("sym", symbol);
        cmd.Parameters.AddWithValue("tf", timeframe);
        cmd.Parameters.AddWithValue("feature_version", featureVersion);
        cmd.Parameters.AddWithValue("stale_interval", $"{Math.Max(1, staleUnlinkedHours)} hours");

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return new MlOutcomeQualityRaw(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        return new MlOutcomeQualityRaw(
            TotalOutcomes: reader.GetInt32(0),
            Wins: reader.GetInt32(1),
            Losses: reader.GetInt32(2),
            Pending: reader.GetInt32(3),
            Expired: reader.GetInt32(4),
            Ambiguous: reader.GetInt32(5),
            InconsistentPnlLabels: reader.GetInt32(6),
            ConflictingTpSlHits: reader.GetInt32(7),
            ClosedTimestampMissing: reader.GetInt32(8),
            TotalFeatureSnapshots: reader.GetInt32(9),
            LinkedFeatureSnapshots: reader.GetInt32(10),
            LabeledFeatureSnapshots: reader.GetInt32(11),
            PendingLinkSnapshots: reader.GetInt32(12),
            StalePendingLinkSnapshots: reader.GetInt32(13),
            ExpectedNoSignalSnapshots: reader.GetInt32(14),
            MlFilteredSnapshots: reader.GetInt32(15),
            OperationallyBlockedSnapshots: reader.GetInt32(16),
            TrainableFeatureSnapshots: reader.GetInt32(17));
    }

    public async Task<IReadOnlyList<MlPredictionOutcomeSample>> GetPredictionOutcomeSamplesAsync(
        string symbol,
        string timeframe,
        string? modelVersion,
        int days,
        int limit,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = @"
            SELECT
                p.created_at_utc,
                COALESCE(p.calibrated_win_probability, p.predicted_win_probability),
                p.recommended_threshold,
                p.model_version,
                o.outcome_label
            FROM ""ETH"".ml_predictions p
            JOIN ""ETH"".ml_feature_snapshots f ON f.evaluation_id = p.evaluation_id
            JOIN ""ETH"".signal_outcomes o ON o.signal_id = p.signal_id
            WHERE f.symbol = @sym
              AND f.timeframe = @tf
              AND o.outcome_label IN ('WIN', 'LOSS')
              AND p.created_at_utc >= NOW() - @window::interval";

        if (!string.IsNullOrWhiteSpace(modelVersion))
            sql += " AND p.model_version = @model_version";

        sql += @"
            ORDER BY p.created_at_utc DESC
            LIMIT @limit;";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("sym", symbol);
        cmd.Parameters.AddWithValue("tf", timeframe);
        cmd.Parameters.AddWithValue("window", $"{Math.Max(1, days)} days");
        cmd.Parameters.AddWithValue("limit", limit);
        if (!string.IsNullOrWhiteSpace(modelVersion))
            cmd.Parameters.AddWithValue("model_version", modelVersion);

        var samples = new List<MlPredictionOutcomeSample>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            samples.Add(new MlPredictionOutcomeSample(
                PredictionTimeUtc: reader.GetFieldValue<DateTimeOffset>(0),
                CalibratedWinProbability: reader.GetDecimal(1),
                RecommendedThreshold: reader.GetInt32(2),
                ModelVersion: reader.GetString(3),
                ActualWin: string.Equals(reader.GetString(4), "WIN", StringComparison.OrdinalIgnoreCase)));
        }

        return samples;
    }

    public Task<IReadOnlyList<MlFeatureSnapshotSample>> GetLabeledFeatureSamplesAsync(
        string symbol,
        string timeframe,
        string featureVersion,
        int limit,
        CancellationToken ct = default)
    {
        const string sql = @"
            SELECT
                f.evaluation_id,
                f.timestamp_utc,
                f.features_json
            FROM ""ETH"".ml_feature_snapshots f
            JOIN ""ETH"".signal_outcomes o ON o.signal_id = f.signal_id
            WHERE f.symbol = @sym
              AND f.timeframe = @tf
              AND f.feature_version = @feature_version
              AND o.outcome_label IN ('WIN', 'LOSS')
            ORDER BY f.created_at_utc DESC
            LIMIT @limit;";

        return ReadFeatureSamplesAsync(sql, symbol, timeframe, featureVersion, window: null, limit, ct);
    }

    public Task<IReadOnlyList<MlFeatureSnapshotSample>> GetRecentFeatureSamplesAsync(
        string symbol,
        string timeframe,
        string featureVersion,
        int hours,
        int limit,
        CancellationToken ct = default)
    {
        const string sql = @"
            SELECT
                f.evaluation_id,
                f.timestamp_utc,
                f.features_json
            FROM ""ETH"".ml_feature_snapshots f
            WHERE f.symbol = @sym
              AND f.timeframe = @tf
              AND f.feature_version = @feature_version
              AND COALESCE(f.link_status, 'PENDING') IN ('PENDING', 'SIGNAL_LINKED', 'ML_FILTERED')
              AND f.created_at_utc >= NOW() - @window::interval
            ORDER BY f.created_at_utc DESC
            LIMIT @limit;";

        return ReadFeatureSamplesAsync(sql, symbol, timeframe, featureVersion, $"{Math.Max(1, hours)} hours", limit, ct);
    }

    private async Task<IReadOnlyList<MlFeatureSnapshotSample>> ReadFeatureSamplesAsync(
        string sql,
        string symbol,
        string timeframe,
        string featureVersion,
        string? window,
        int limit,
        CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("sym", symbol);
        cmd.Parameters.AddWithValue("tf", timeframe);
        cmd.Parameters.AddWithValue("feature_version", featureVersion);
        cmd.Parameters.AddWithValue("limit", limit);
        if (window != null)
            cmd.Parameters.AddWithValue("window", window);

        var samples = new List<MlFeatureSnapshotSample>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            samples.Add(new MlFeatureSnapshotSample(
                EvaluationId: reader.GetGuid(0),
                TimestampUtc: reader.GetFieldValue<DateTimeOffset>(1),
                Features: ReadFeatures(reader.GetString(2))));
        }

        return samples;
    }

    private static IReadOnlyDictionary<string, double> ReadFeatures(string json)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Number &&
                property.Value.TryGetDouble(out var numeric))
            {
                result[property.Name] = numeric;
            }
            else if (property.Value.ValueKind == JsonValueKind.True)
            {
                result[property.Name] = 1d;
            }
            else if (property.Value.ValueKind == JsonValueKind.False)
            {
                result[property.Name] = 0d;
            }
        }

        return result;
    }
}
