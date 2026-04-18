using EthSignal.Domain.Models;
using Npgsql;
using NpgsqlTypes;

namespace EthSignal.Infrastructure.Db;

public sealed class MlPredictionRepository : IMlPredictionRepository
{
    private readonly string _connectionString;

    public MlPredictionRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task InsertAsync(MlPrediction prediction, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO ""ETH"".ml_predictions
                (prediction_id, evaluation_id, signal_id, model_version, model_type,
                 predicted_win_probability, calibrated_confidence,
                 raw_win_probability, calibrated_win_probability, prediction_confidence,
                 recommended_threshold,
                 expected_value_r, inference_latency_us, is_active, mode, created_at_utc)
            VALUES (@pid, @eid, @sid, @mv, @mt,
                    @legacyPwp, @legacyConf, @rawPwp, @calPwp, @predConf, @rt,
                    @ev, @lat, @ia, @mode, NOW())
            ON CONFLICT DO NOTHING;", conn);

        cmd.Parameters.AddWithValue("pid", prediction.PredictionId);
        cmd.Parameters.AddWithValue("eid", prediction.EvaluationId);
        cmd.Parameters.AddWithValue("sid", prediction.SignalId.HasValue ? prediction.SignalId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("mv", prediction.ModelVersion);
        cmd.Parameters.AddWithValue("mt", prediction.ModelType);
        cmd.Parameters.AddWithValue("legacyPwp", prediction.CalibratedWinProbability);
        cmd.Parameters.AddWithValue("legacyConf", prediction.PredictionConfidence);
        cmd.Parameters.AddWithValue("rawPwp", prediction.RawWinProbability);
        cmd.Parameters.AddWithValue("calPwp", prediction.CalibratedWinProbability);
        cmd.Parameters.AddWithValue("predConf", prediction.PredictionConfidence);
        cmd.Parameters.AddWithValue("rt", prediction.RecommendedThreshold);
        cmd.Parameters.AddWithValue("ev", prediction.ExpectedValueR);
        cmd.Parameters.AddWithValue("lat", prediction.InferenceLatencyUs);
        cmd.Parameters.AddWithValue("ia", prediction.IsActive);
        cmd.Parameters.AddWithValue("mode", prediction.Mode.ToString());

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateSignalIdAsync(Guid evaluationId, Guid signalId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            UPDATE ""ETH"".ml_predictions
            SET signal_id = @sid
            WHERE evaluation_id = @eid AND signal_id IS NULL;", conn);

        cmd.Parameters.AddWithValue("sid", signalId);
        cmd.Parameters.AddWithValue("eid", evaluationId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<MlPrediction?> GetLatestAsync(
        string symbol,
        string? timeframe = null,
        string scope = "all",
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand($@"
            SELECT p.prediction_id, p.evaluation_id, p.signal_id, f.timeframe, f.link_status,
                   p.model_version, p.model_type,
                   COALESCE(p.raw_win_probability, p.predicted_win_probability),
                   COALESCE(p.calibrated_win_probability, p.predicted_win_probability),
                   COALESCE(p.prediction_confidence, p.calibrated_confidence),
                   p.recommended_threshold,
                   p.expected_value_r, p.inference_latency_us, p.is_active, p.mode, p.created_at_utc
            FROM ""ETH"".ml_predictions p
            JOIN ""ETH"".ml_feature_snapshots f ON p.evaluation_id = f.evaluation_id
            WHERE f.symbol = @symbol
              AND (@timeframe IS NULL OR f.timeframe = @timeframe)
              {BuildScopeClause(scope)}
            ORDER BY p.created_at_utc DESC
            LIMIT 1;", conn);

        cmd.Parameters.AddWithValue("symbol", symbol);
        cmd.Parameters.Add(new NpgsqlParameter("timeframe", NpgsqlDbType.Text) { Value = (object?)timeframe ?? DBNull.Value });

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadPrediction(reader);
    }

    public async Task<IReadOnlyList<MlPrediction>> GetRecentAsync(
        string symbol,
        int hours,
        int limit,
        string? timeframe = null,
        string scope = "linked",
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand($@"
            SELECT p.prediction_id, p.evaluation_id, p.signal_id, f.timeframe, f.link_status,
                   p.model_version, p.model_type,
                   COALESCE(p.raw_win_probability, p.predicted_win_probability),
                   COALESCE(p.calibrated_win_probability, p.predicted_win_probability),
                   COALESCE(p.prediction_confidence, p.calibrated_confidence),
                   p.recommended_threshold,
                   p.expected_value_r, p.inference_latency_us, p.is_active, p.mode, p.created_at_utc
            FROM ""ETH"".ml_predictions p
            JOIN ""ETH"".ml_feature_snapshots f ON p.evaluation_id = f.evaluation_id
            WHERE f.symbol = @symbol
              AND (@timeframe IS NULL OR f.timeframe = @timeframe)
              AND p.created_at_utc >= NOW() - @interval::interval
              {BuildScopeClause(scope)}
            ORDER BY p.created_at_utc DESC
            LIMIT @limit;", conn);

        cmd.Parameters.AddWithValue("symbol", symbol);
        cmd.Parameters.Add(new NpgsqlParameter("timeframe", NpgsqlDbType.Text) { Value = (object?)timeframe ?? DBNull.Value });
        cmd.Parameters.AddWithValue("interval", $"{hours} hours");
        cmd.Parameters.AddWithValue("limit", limit);

        var results = new List<MlPrediction>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadPrediction(reader));
        return results;
    }

    public async Task<MlPrediction?> GetBySignalIdAsync(Guid signalId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT p.prediction_id, p.evaluation_id, p.signal_id, f.timeframe, f.link_status,
                   p.model_version, p.model_type,
                   COALESCE(p.raw_win_probability, p.predicted_win_probability),
                   COALESCE(p.calibrated_win_probability, p.predicted_win_probability),
                   COALESCE(p.prediction_confidence, p.calibrated_confidence),
                   p.recommended_threshold,
                   p.expected_value_r, p.inference_latency_us, p.is_active, p.mode, p.created_at_utc
            FROM ""ETH"".ml_predictions p
            LEFT JOIN ""ETH"".ml_feature_snapshots f ON f.evaluation_id = p.evaluation_id
            WHERE p.signal_id = @sid
            ORDER BY p.created_at_utc DESC
            LIMIT 1;", conn);

        cmd.Parameters.AddWithValue("sid", signalId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadPrediction(reader);
    }

    private static MlPrediction ReadPrediction(NpgsqlDataReader reader)
    {
        return new MlPrediction
        {
            PredictionId = reader.GetGuid(0),
            EvaluationId = reader.GetGuid(1),
            SignalId = reader.IsDBNull(2) ? null : reader.GetGuid(2),
            Timeframe = reader.IsDBNull(3) ? null : reader.GetString(3),
            LinkStatus = reader.IsDBNull(4) ? null : reader.GetString(4),
            ModelVersion = reader.GetString(5),
            ModelType = reader.GetString(6),
            RawWinProbability = reader.GetDecimal(7),
            CalibratedWinProbability = reader.GetDecimal(8),
            PredictionConfidence = reader.GetInt32(9),
            RecommendedThreshold = reader.GetInt32(10),
            ExpectedValueR = reader.GetDecimal(11),
            InferenceLatencyUs = reader.GetInt32(12),
            IsActive = reader.GetBoolean(13),
            Mode = Enum.Parse<MlMode>(reader.GetString(14), ignoreCase: true),
            CreatedAtUtc = reader.GetFieldValue<DateTimeOffset>(15)
        };
    }

    private static string BuildScopeClause(string scope)
    {
        return NormalizeScope(scope) switch
        {
            "linked" => "AND COALESCE(f.link_status, 'PENDING') = 'SIGNAL_LINKED'",
            "actionable" => "AND COALESCE(f.link_status, 'PENDING') IN ('SIGNAL_LINKED', 'ML_FILTERED')",
            _ => string.Empty
        };
    }

    private static string NormalizeScope(string? scope) => scope?.Trim().ToLowerInvariant() switch
    {
        "linked" => "linked",
        "actionable" => "actionable",
        _ => "all"
    };
}
