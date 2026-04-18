using System.Text.Json;
using EthSignal.Domain.Models;
using Npgsql;
using NpgsqlTypes;

namespace EthSignal.Infrastructure.Db;

public sealed class MlModelRepository : IMlModelRepository
{
    private readonly string _connectionString;

    public MlModelRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<MlModelMetadata?> GetActiveModelAsync(string modelType, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT id, model_type, model_version, file_path, file_format,
                   train_start_utc, train_end_utc, training_sample_count, feature_count,
                   feature_list_json, auc_roc, brier_score, ece, log_loss,
                   fold_metrics_json, feature_importance_json, status,
                   created_at_utc, activated_at_utc, retired_at_utc, retired_reason
            FROM ""ETH"".ml_models
            WHERE model_type = @type AND LOWER(status) = 'active'
            ORDER BY activated_at_utc DESC NULLS LAST
            LIMIT 1;", conn);

        cmd.Parameters.AddWithValue("type", modelType);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadModel(reader);
    }

    public async Task<MlModelMetadata?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT id, model_type, model_version, file_path, file_format,
                   train_start_utc, train_end_utc, training_sample_count, feature_count,
                   feature_list_json, auc_roc, brier_score, ece, log_loss,
                   fold_metrics_json, feature_importance_json, status,
                   created_at_utc, activated_at_utc, retired_at_utc, retired_reason
            FROM ""ETH"".ml_models
            WHERE id = @id;", conn);

        cmd.Parameters.AddWithValue("id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadModel(reader);
    }

    public async Task<IReadOnlyList<MlModelMetadata>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT id, model_type, model_version, file_path, file_format,
                   train_start_utc, train_end_utc, training_sample_count, feature_count,
                   feature_list_json, auc_roc, brier_score, ece, log_loss,
                   fold_metrics_json, feature_importance_json, status,
                   created_at_utc, activated_at_utc, retired_at_utc, retired_reason
            FROM ""ETH"".ml_models
            ORDER BY created_at_utc DESC;", conn);

        var results = new List<MlModelMetadata>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadModel(reader));
        return results;
    }

    public async Task<long> InsertAsync(MlModelMetadata model, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO ""ETH"".ml_models
                (model_type, model_version, file_path, file_format,
                 train_start_utc, train_end_utc, training_sample_count, feature_count,
                 feature_list_json, auc_roc, brier_score, ece, log_loss,
                 fold_metrics_json, feature_importance_json, status, created_at_utc)
            VALUES (@type, @ver, @path, @fmt,
                    @ts, @te, @cnt, @fc,
                    @fl, @auc, @brier, @ece, @ll,
                    @fm, @fi, @status, NOW())
            RETURNING id;", conn);

        cmd.Parameters.AddWithValue("type", model.ModelType);
        cmd.Parameters.AddWithValue("ver", model.ModelVersion);
        cmd.Parameters.AddWithValue("path", model.FilePath);
        cmd.Parameters.AddWithValue("fmt", model.FileFormat);
        cmd.Parameters.AddWithValue("ts", model.TrainStartUtc);
        cmd.Parameters.AddWithValue("te", model.TrainEndUtc);
        cmd.Parameters.AddWithValue("cnt", model.TrainingSampleCount);
        cmd.Parameters.AddWithValue("fc", model.FeatureCount);
        cmd.Parameters.Add(new NpgsqlParameter("fl", NpgsqlDbType.Jsonb) { Value = model.FeatureListJson });
        cmd.Parameters.AddWithValue("auc", model.AucRoc);
        cmd.Parameters.AddWithValue("brier", model.BrierScore);
        cmd.Parameters.AddWithValue("ece", model.ExpectedCalibrationError);
        cmd.Parameters.AddWithValue("ll", model.LogLoss);
        cmd.Parameters.Add(new NpgsqlParameter("fm", NpgsqlDbType.Jsonb) { Value = model.FoldMetricsJson });
        cmd.Parameters.Add(new NpgsqlParameter("fi", NpgsqlDbType.Jsonb) { Value = model.FeatureImportanceJson });
        cmd.Parameters.AddWithValue("status", model.Status.ToString().ToLowerInvariant());

        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task UpdateStatusAsync(long id, MlModelStatus status, string? reason = null, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = status switch
        {
            MlModelStatus.Active => @"UPDATE ""ETH"".ml_models SET status = 'active', activated_at_utc = NOW() WHERE id = @id;",
            MlModelStatus.Retired => @"UPDATE ""ETH"".ml_models SET status = 'retired', retired_at_utc = NOW(), retired_reason = @reason WHERE id = @id;",
            _ => @$"UPDATE ""ETH"".ml_models SET status = '{status.ToString().ToLowerInvariant()}' WHERE id = @id;"
        };

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        if (reason != null)
            cmd.Parameters.AddWithValue("reason", reason);
        else
            cmd.Parameters.AddWithValue("reason", DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static MlModelMetadata ReadModel(Npgsql.NpgsqlDataReader reader)
    {
        var model = new MlModelMetadata
        {
            Id = reader.GetInt64(0),
            ModelType = reader.GetString(1),
            ModelVersion = reader.GetString(2),
            FilePath = reader.GetString(3),
            FileFormat = reader.GetString(4),
            TrainStartUtc = reader.GetFieldValue<DateTimeOffset>(5),
            TrainEndUtc = reader.GetFieldValue<DateTimeOffset>(6),
            TrainingSampleCount = reader.GetInt32(7),
            FeatureCount = reader.GetInt32(8),
            FeatureListJson = reader.GetString(9),
            AucRoc = reader.IsDBNull(10) ? 0 : reader.GetDecimal(10),
            BrierScore = reader.IsDBNull(11) ? 0 : reader.GetDecimal(11),
            ExpectedCalibrationError = reader.IsDBNull(12) ? 0 : reader.GetDecimal(12),
            LogLoss = reader.IsDBNull(13) ? 0 : reader.GetDecimal(13),
            FoldMetricsJson = reader.IsDBNull(14) ? "{}" : reader.GetString(14),
            FeatureImportanceJson = reader.IsDBNull(15) ? "{}" : reader.GetString(15),
            Status = Enum.Parse<MlModelStatus>(reader.GetString(16), ignoreCase: true),
            CreatedAtUtc = reader.GetFieldValue<DateTimeOffset>(17),
            ActivatedAtUtc = reader.IsDBNull(18) ? null : reader.GetFieldValue<DateTimeOffset>(18),
            RetiredAtUtc = reader.IsDBNull(19) ? null : reader.GetFieldValue<DateTimeOffset>(19),
            RetiredReason = reader.IsDBNull(20) ? null : reader.GetString(20)
        };

        return MlModelMetadataSidecarReader.Enrich(model);
    }
}
