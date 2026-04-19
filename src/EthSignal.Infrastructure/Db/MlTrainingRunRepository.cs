using Npgsql;
using EthSignal.Infrastructure.Engine.ML;

namespace EthSignal.Infrastructure.Db;

public sealed class MlTrainingRunRepository : IMlTrainingRunRepository
{
    private readonly string _connectionString;

    public MlTrainingRunRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<long> InsertAsync(MlTrainingRunRecord run, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO ""ETH"".ml_training_runs
                (model_type, trigger, data_start_utc, data_end_utc, sample_count,
                 fold_count, embargo_bars, status, started_at_utc)
            VALUES (@mt, @trig, @ds, @de, @sc, @fc, @eb, @st, NOW())
            RETURNING id;", conn);
        cmd.Parameters.AddWithValue("mt", run.ModelType);
        cmd.Parameters.AddWithValue("trig", run.Trigger);
        cmd.Parameters.AddWithValue("ds", run.DataStartUtc);
        cmd.Parameters.AddWithValue("de", run.DataEndUtc);
        cmd.Parameters.AddWithValue("sc", (object?)run.SampleCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("fc", run.FoldCount);
        cmd.Parameters.AddWithValue("eb", run.EmbargoMars);
        cmd.Parameters.AddWithValue("st", run.Status);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task UpdateAsync(long id, string status, int? sampleCount, long? resultModelId,
        string? errorText, int? durationSeconds, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            UPDATE ""ETH"".ml_training_runs
            SET status = @st,
                sample_count = @sc,
                result_model_id = @rm,
                error_text = @err,
                duration_seconds = @dur,
                finished_at_utc = NOW()
            WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("st", status);
        cmd.Parameters.AddWithValue("sc", (object?)sampleCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("rm", (object?)resultModelId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("err", (object?)errorText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("dur", (object?)durationSeconds ?? DBNull.Value);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<MlTrainingRunRecord>> GetRecentAsync(int limit = 20, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, model_type, trigger, data_start_utc, data_end_utc, sample_count,
                   fold_count, embargo_bars, status, result_model_id, error_text,
                   started_at_utc, finished_at_utc, duration_seconds
            FROM ""ETH"".ml_training_runs
            ORDER BY started_at_utc DESC
            LIMIT @lim;", conn);
        cmd.Parameters.AddWithValue("lim", limit);
        var results = new List<MlTrainingRunRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadRun(reader));
        return results;
    }

    public async Task<MlTrainingRunRecord?> GetLatestSuccessfulAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, model_type, trigger, data_start_utc, data_end_utc, sample_count,
                   fold_count, embargo_bars, status, result_model_id, error_text,
                   started_at_utc, finished_at_utc, duration_seconds
            FROM ""ETH"".ml_training_runs
            WHERE status = 'success'
            ORDER BY finished_at_utc DESC
            LIMIT 1;", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadRun(reader);
    }

    public async Task<MlTrainingRunRecord?> GetLatestCompletedAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, model_type, trigger, data_start_utc, data_end_utc, sample_count,
                   fold_count, embargo_bars, status, result_model_id, error_text,
                   started_at_utc, finished_at_utc, duration_seconds
            FROM ""ETH"".ml_training_runs
            WHERE status <> 'running'
              AND finished_at_utc IS NOT NULL
            ORDER BY finished_at_utc DESC
            LIMIT 1;", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadRun(reader);
    }

    public async Task<int> GetLabeledSampleCountAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            WITH trainable AS (
                SELECT f.evaluation_id, o.outcome_label
                FROM ""ETH"".ml_feature_snapshots f
                JOIN ""ETH"".signal_outcomes o ON o.signal_id = f.signal_id
                WHERE f.feature_version = @feature_version
                  AND f.signal_id IS NOT NULL
                  AND COALESCE(f.timeframe, '') <> '1m'
                  AND COALESCE(f.link_status, 'SIGNAL_LINKED') = 'SIGNAL_LINKED'
                  AND o.outcome_label IN ('WIN','LOSS')
                  AND COALESCE((f.features_json ->> 'direction_encoded')::INT, 0) <> 0
                UNION
                SELECT f.evaluation_id, b.outcome_label
                FROM ""ETH"".ml_feature_snapshots f
                JOIN ""ETH"".blocked_signal_outcomes b ON b.evaluation_id = f.evaluation_id
                JOIN ""ETH"".signal_decision_audit d ON d.evaluation_id = b.evaluation_id
                WHERE f.feature_version = @feature_version
                  AND COALESCE(f.timeframe, '') <> '1m'
                  AND d.outcome_category = 'OPERATIONAL_BLOCKED'
                  AND b.outcome_label IN ('WIN','LOSS')
                  AND COALESCE((f.features_json ->> 'direction_encoded')::INT, 0) <> 0
                UNION
                SELECT f.evaluation_id, g.outcome_label
                FROM ""ETH"".ml_feature_snapshots f
                JOIN ""ETH"".generated_signal_outcomes g ON g.evaluation_id = f.evaluation_id
                WHERE f.feature_version = @feature_version
                  AND COALESCE(f.timeframe, '') <> '1m'
                  AND g.outcome_label IN ('WIN','LOSS')
                  AND COALESCE((f.features_json ->> 'direction_encoded')::INT, 0) <> 0
            )
            SELECT COUNT(*)::INT
            FROM trainable;", conn);
        cmd.Parameters.AddWithValue("feature_version", MlFeatureExtractor.FeatureVersion);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<(int wins, int losses)> GetWinLossCountsAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            WITH trainable AS (
                SELECT f.evaluation_id, o.outcome_label
                FROM ""ETH"".ml_feature_snapshots f
                JOIN ""ETH"".signal_outcomes o ON o.signal_id = f.signal_id
                WHERE f.feature_version = @feature_version
                  AND f.signal_id IS NOT NULL
                  AND COALESCE(f.timeframe, '') <> '1m'
                  AND COALESCE(f.link_status, 'SIGNAL_LINKED') = 'SIGNAL_LINKED'
                  AND o.outcome_label IN ('WIN','LOSS')
                  AND COALESCE((f.features_json ->> 'direction_encoded')::INT, 0) <> 0
                UNION
                SELECT f.evaluation_id, b.outcome_label
                FROM ""ETH"".ml_feature_snapshots f
                JOIN ""ETH"".blocked_signal_outcomes b ON b.evaluation_id = f.evaluation_id
                JOIN ""ETH"".signal_decision_audit d ON d.evaluation_id = b.evaluation_id
                WHERE f.feature_version = @feature_version
                  AND COALESCE(f.timeframe, '') <> '1m'
                  AND d.outcome_category = 'OPERATIONAL_BLOCKED'
                  AND b.outcome_label IN ('WIN','LOSS')
                  AND COALESCE((f.features_json ->> 'direction_encoded')::INT, 0) <> 0
                UNION
                SELECT f.evaluation_id, g.outcome_label
                FROM ""ETH"".ml_feature_snapshots f
                JOIN ""ETH"".generated_signal_outcomes g ON g.evaluation_id = f.evaluation_id
                WHERE f.feature_version = @feature_version
                  AND COALESCE(f.timeframe, '') <> '1m'
                  AND g.outcome_label IN ('WIN','LOSS')
                  AND COALESCE((f.features_json ->> 'direction_encoded')::INT, 0) <> 0
            )
            SELECT
                COUNT(*) FILTER (WHERE outcome_label = 'WIN')::INT,
                COUNT(*) FILTER (WHERE outcome_label = 'LOSS')::INT
            FROM trainable;", conn);
        cmd.Parameters.AddWithValue("feature_version", MlFeatureExtractor.FeatureVersion);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    public async Task<int> GetNewOutcomesSinceAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            WITH trainable AS (
                SELECT f.evaluation_id, o.outcome_label, o.evaluated_at_utc
                FROM ""ETH"".ml_feature_snapshots f
                JOIN ""ETH"".signal_outcomes o ON o.signal_id = f.signal_id
                WHERE f.feature_version = @feature_version
                  AND f.signal_id IS NOT NULL
                  AND COALESCE(f.timeframe, '') <> '1m'
                  AND COALESCE(f.link_status, 'SIGNAL_LINKED') = 'SIGNAL_LINKED'
                  AND o.outcome_label IN ('WIN','LOSS')
                  AND COALESCE((f.features_json ->> 'direction_encoded')::INT, 0) <> 0
                UNION
                SELECT f.evaluation_id, b.outcome_label, b.evaluated_at_utc
                FROM ""ETH"".ml_feature_snapshots f
                JOIN ""ETH"".blocked_signal_outcomes b ON b.evaluation_id = f.evaluation_id
                JOIN ""ETH"".signal_decision_audit d ON d.evaluation_id = b.evaluation_id
                WHERE f.feature_version = @feature_version
                  AND COALESCE(f.timeframe, '') <> '1m'
                  AND d.outcome_category = 'OPERATIONAL_BLOCKED'
                  AND b.outcome_label IN ('WIN','LOSS')
                  AND COALESCE((f.features_json ->> 'direction_encoded')::INT, 0) <> 0
                UNION
                SELECT f.evaluation_id, g.outcome_label, g.evaluated_at_utc
                FROM ""ETH"".ml_feature_snapshots f
                JOIN ""ETH"".generated_signal_outcomes g ON g.evaluation_id = f.evaluation_id
                WHERE f.feature_version = @feature_version
                  AND COALESCE(f.timeframe, '') <> '1m'
                  AND g.outcome_label IN ('WIN','LOSS')
                  AND COALESCE((f.features_json ->> 'direction_encoded')::INT, 0) <> 0
            )
            SELECT COUNT(*)::INT
            FROM trainable
            WHERE evaluated_at_utc > @since;", conn);
        cmd.Parameters.AddWithValue("since", since);
        cmd.Parameters.AddWithValue("feature_version", MlFeatureExtractor.FeatureVersion);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static MlTrainingRunRecord ReadRun(NpgsqlDataReader r) => new()
    {
        Id = r.GetInt64(0),
        ModelType = r.GetString(1),
        Trigger = r.GetString(2),
        DataStartUtc = r.GetFieldValue<DateTimeOffset>(3),
        DataEndUtc = r.GetFieldValue<DateTimeOffset>(4),
        SampleCount = r.IsDBNull(5) ? null : r.GetInt32(5),
        FoldCount = r.GetInt32(6),
        EmbargoMars = r.GetInt32(7),
        Status = r.GetString(8),
        ResultModelId = r.IsDBNull(9) ? null : r.GetInt64(9),
        ErrorText = r.IsDBNull(10) ? null : r.GetString(10),
        StartedAtUtc = r.GetFieldValue<DateTimeOffset>(11),
        FinishedAtUtc = r.IsDBNull(12) ? null : r.GetFieldValue<DateTimeOffset>(12),
        DurationSeconds = r.IsDBNull(13) ? null : r.GetInt32(13)
    };
}
