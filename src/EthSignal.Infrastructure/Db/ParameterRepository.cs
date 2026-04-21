using System.Text.Json;
using EthSignal.Domain.Models;
using Npgsql;
using NpgsqlTypes;

namespace EthSignal.Infrastructure.Db;

public sealed class ParameterRepository : IParameterRepository
{
    private readonly string _connectionString;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ParameterRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    private Task EnsureSchemaAsync(CancellationToken ct) =>
        RuntimeDbSchemaGuard.EnsureMigratedAsync(_connectionString, ct);

    public async Task<StrategyParameterSet?> GetActiveAsync(string strategyVersion, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT id, strategy_version, parameter_hash, parameters_json, status,
                   created_utc, created_by, activated_utc, retired_utc, notes,
                   parent_parameter_set_id, objective_function_version, code_version
            FROM ""ETH"".strategy_parameter_sets
            WHERE strategy_version = @sv AND status = 'Active'
            ORDER BY activated_utc DESC NULLS LAST, created_utc DESC, id DESC
            LIMIT 1;", conn);
        cmd.Parameters.AddWithValue("sv", strategyVersion);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return ReadSet(r);
    }

    public async Task<StrategyParameterSet?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT id, strategy_version, parameter_hash, parameters_json, status,
                   created_utc, created_by, activated_utc, retired_utc, notes,
                   parent_parameter_set_id, objective_function_version, code_version
            FROM ""ETH"".strategy_parameter_sets
            WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("id", id);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return ReadSet(r);
    }

    public async Task<long> InsertAsync(StrategyParameterSet set, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO ""ETH"".strategy_parameter_sets
                (strategy_version, parameter_hash, parameters_json, status,
                 created_utc, created_by, notes, parent_parameter_set_id,
                 objective_function_version, code_version)
            VALUES (@sv, @hash, @json::jsonb, @status, @created, @by, @notes,
                    @parent, @ofv, @cv)
            ON CONFLICT (parameter_hash) DO UPDATE SET status = EXCLUDED.status
            RETURNING id;", conn);

        cmd.Parameters.AddWithValue("sv", set.StrategyVersion);
        cmd.Parameters.AddWithValue("hash", set.ParameterHash);
        cmd.Parameters.Add(new NpgsqlParameter("json", NpgsqlDbType.Text) { Value = JsonSerializer.Serialize(set.Parameters, JsonOpts) });
        cmd.Parameters.AddWithValue("status", set.Status.ToString());
        cmd.Parameters.AddWithValue("created", set.CreatedUtc);
        cmd.Parameters.AddWithValue("by", (object?)set.CreatedBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("notes", (object?)set.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("parent", (object?)set.ParentParameterSetId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ofv", (object?)set.ObjectiveFunctionVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("cv", (object?)set.CodeVersion ?? DBNull.Value);

        var result = await cmd.ExecuteScalarAsync(ct);
        return (long)result!;
    }

    public async Task ActivateAsync(long id, long? previousId, string? activatedBy, string? reason, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using var retireCmd = new NpgsqlCommand(@"
            UPDATE ""ETH"".strategy_parameter_sets
            SET status = 'Retired', retired_utc = NOW()
            WHERE status = 'Active'
              AND id != @id
              AND strategy_version = (
                  SELECT strategy_version
                  FROM ""ETH"".strategy_parameter_sets
                  WHERE id = @id
              );", conn, tx);
        retireCmd.Parameters.AddWithValue("id", id);
        await retireCmd.ExecuteNonQueryAsync(ct);

        await using var activateCmd = new NpgsqlCommand(@"
            UPDATE ""ETH"".strategy_parameter_sets
            SET status = 'Active', activated_utc = NOW(), retired_utc = NULL
            WHERE id = @id;", conn, tx);
        activateCmd.Parameters.AddWithValue("id", id);
        await activateCmd.ExecuteNonQueryAsync(ct);

        await using var dedupCmd = new NpgsqlCommand(@"
            SELECT 1 FROM ""ETH"".parameter_activation_history
            WHERE parameter_set_id = @id
            ORDER BY activated_utc DESC LIMIT 1;", conn, tx);
        dedupCmd.Parameters.AddWithValue("id", id);
        var alreadyLatest = await dedupCmd.ExecuteScalarAsync(ct) != null;

        if (!alreadyLatest || previousId != null)
        {
            await using var histCmd = new NpgsqlCommand(@"
                INSERT INTO ""ETH"".parameter_activation_history
                    (parameter_set_id, previous_set_id, activated_utc, activated_by, promotion_reason)
                VALUES (@id, @prev, NOW(), @by, @reason);", conn, tx);
            histCmd.Parameters.AddWithValue("id", id);
            histCmd.Parameters.AddWithValue("prev", (object?)previousId ?? DBNull.Value);
            histCmd.Parameters.AddWithValue("by", (object?)activatedBy ?? DBNull.Value);
            histCmd.Parameters.AddWithValue("reason", (object?)reason ?? DBNull.Value);
            await histCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task UpdateStatusAsync(long id, ParameterSetStatus status, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            UPDATE ""ETH"".strategy_parameter_sets SET status = @status WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("status", status.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<StrategyParameterSet>> GetCandidatesAsync(string strategyVersion, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT id, strategy_version, parameter_hash, parameters_json, status,
                   created_utc, created_by, activated_utc, retired_utc, notes,
                   parent_parameter_set_id, objective_function_version, code_version
            FROM ""ETH"".strategy_parameter_sets
            WHERE strategy_version = @sv AND status = 'Candidate'
            ORDER BY created_utc DESC;", conn);
        cmd.Parameters.AddWithValue("sv", strategyVersion);

        var results = new List<StrategyParameterSet>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            results.Add(ReadSet(r));
        return results;
    }

    private static StrategyParameterSet ReadSet(NpgsqlDataReader r)
    {
        var paramsJson = r.GetString(3);
        var parameters = JsonSerializer.Deserialize<StrategyParameters>(paramsJson, JsonOpts)
            ?? StrategyParameters.Default;

        return new StrategyParameterSet
        {
            Id = r.GetInt64(0),
            StrategyVersion = r.GetString(1),
            ParameterHash = r.GetString(2),
            Parameters = parameters,
            Status = Enum.Parse<ParameterSetStatus>(r.GetString(4)),
            CreatedUtc = r.GetFieldValue<DateTimeOffset>(5),
            CreatedBy = r.IsDBNull(6) ? null : r.GetString(6),
            ActivatedUtc = r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7),
            RetiredUtc = r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8),
            Notes = r.IsDBNull(9) ? null : r.GetString(9),
            ParentParameterSetId = r.IsDBNull(10) ? null : r.GetInt64(10),
            ObjectiveFunctionVersion = r.IsDBNull(11) ? null : r.GetString(11),
            CodeVersion = r.IsDBNull(12) ? null : r.GetString(12)
        };
    }
}
