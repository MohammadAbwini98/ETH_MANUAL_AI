using System.Text.Json;
using EthSignal.Domain.Models;
using Npgsql;
using NpgsqlTypes;

namespace EthSignal.Infrastructure.Db;

public sealed class MlFeatureRepository : IMlFeatureRepository
{
    private readonly string _connectionString;

    public MlFeatureRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task InsertAsync(
        MlFeatureVector features,
        Guid? signalId,
        string featureVersion,
        string linkStatus,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO ""ETH"".ml_feature_snapshots
                (evaluation_id, signal_id, symbol, timeframe, timestamp_utc,
                 features_json, feature_version, link_status, created_at_utc)
            VALUES (@eid, @sid, @sym, @tf, @ts, @fj, @fv, @linkStatus, NOW())
            ON CONFLICT (evaluation_id) DO NOTHING;", conn);

        cmd.Parameters.AddWithValue("eid", features.EvaluationId);
        cmd.Parameters.AddWithValue("sid", signalId.HasValue ? signalId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("sym", features.Symbol);
        cmd.Parameters.AddWithValue("tf", features.Timeframe);
        cmd.Parameters.AddWithValue("ts", features.Timestamp);

        var json = JsonSerializer.Serialize(features.ToFeatureMap());
        cmd.Parameters.Add(new NpgsqlParameter("fj", NpgsqlDbType.Jsonb) { Value = json });
        cmd.Parameters.AddWithValue("fv", featureVersion);
        cmd.Parameters.AddWithValue("linkStatus", linkStatus);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task LinkSignalAsync(Guid evaluationId, Guid signalId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            UPDATE ""ETH"".ml_feature_snapshots
            SET signal_id = @sid,
                link_status = @linkStatus
            WHERE evaluation_id = @eid AND signal_id IS NULL;", conn);

        cmd.Parameters.AddWithValue("sid", signalId);
        cmd.Parameters.AddWithValue("eid", evaluationId);
        cmd.Parameters.AddWithValue("linkStatus", MlEvaluationLinkStatus.SignalLinked);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateLinkStatusAsync(Guid evaluationId, string linkStatus, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            UPDATE ""ETH"".ml_feature_snapshots
            SET link_status = @linkStatus
            WHERE evaluation_id = @eid AND signal_id IS NULL;", conn);

        cmd.Parameters.AddWithValue("eid", evaluationId);
        cmd.Parameters.AddWithValue("linkStatus", linkStatus);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<MlFeatureVector?> GetByEvaluationIdAsync(Guid evaluationId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT features_json FROM ""ETH"".ml_feature_snapshots
            WHERE evaluation_id = @eid;", conn);

        cmd.Parameters.AddWithValue("eid", evaluationId);

        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is null or DBNull) return null;

        return JsonSerializer.Deserialize<MlFeatureVector>(result.ToString()!,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }
}
