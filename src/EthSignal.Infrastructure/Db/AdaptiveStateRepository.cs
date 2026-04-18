using System.Text.Json;
using EthSignal.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace EthSignal.Infrastructure.Db;

/// <summary>
/// Postgres implementation of adaptive state persistence.
///
/// Stores per-condition outcome windows and retrospective overlays so they
/// survive process restarts (Issue #3).
/// </summary>
public sealed class AdaptiveStateRepository : IAdaptiveStateRepository
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly string _connectionString;
    private readonly ILogger<AdaptiveStateRepository> _logger;

    public AdaptiveStateRepository(string connectionString,
        ILogger<AdaptiveStateRepository>? logger = null)
    {
        _connectionString = connectionString;
        _logger = logger ?? NullLogger<AdaptiveStateRepository>.Instance;
    }

    public async Task<IReadOnlyList<OutcomeWindowSnapshot>> LoadOutcomeWindowsAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT condition_key, outcomes_json
            FROM ""ETH"".adaptive_condition_outcomes;", conn);

        var results = new List<OutcomeWindowSnapshot>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var key = r.GetString(0);
            var json = r.GetString(1);
            var outcomes = JsonSerializer.Deserialize<List<SignalOutcome>>(json, JsonOpts)
                           ?? new List<SignalOutcome>();
            results.Add(new OutcomeWindowSnapshot
            {
                ConditionKey = key,
                Outcomes = outcomes
            });
        }
        return results;
    }

    public async Task UpsertOutcomeWindowAsync(string conditionKey,
        IReadOnlyList<SignalOutcome> outcomes, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO ""ETH"".adaptive_condition_outcomes
                    (condition_key, outcomes_json, outcome_count, last_updated_utc)
                VALUES (@key, @json::jsonb, @cnt, NOW())
                ON CONFLICT (condition_key) DO UPDATE SET
                    outcomes_json    = EXCLUDED.outcomes_json,
                    outcome_count    = EXCLUDED.outcome_count,
                    last_updated_utc = EXCLUDED.last_updated_utc;", conn);
            cmd.Parameters.AddWithValue("key", conditionKey);
            cmd.Parameters.Add(new NpgsqlParameter("json", NpgsqlDbType.Text)
            { Value = JsonSerializer.Serialize(outcomes, JsonOpts) });
            cmd.Parameters.AddWithValue("cnt", outcomes.Count);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AdaptiveState] UpsertOutcomeWindow failed for {Key}", conditionKey);
        }
    }

    public async Task<IReadOnlyList<RetrospectiveOverlayRecord>> LoadRetrospectiveOverlaysAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT condition_key, overlay_json
            FROM ""ETH"".adaptive_retrospective_overlays;", conn);

        var results = new List<RetrospectiveOverlayRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var key = r.GetString(0);
            var json = r.GetString(1);
            var overlay = JsonSerializer.Deserialize<ParameterOverlay>(json, JsonOpts);
            if (overlay == null) continue;
            results.Add(new RetrospectiveOverlayRecord
            {
                ConditionKey = key,
                Overlay = overlay
            });
        }
        return results;
    }

    public async Task UpsertRetrospectiveOverlayAsync(string conditionKey,
        ParameterOverlay overlay, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO ""ETH"".adaptive_retrospective_overlays
                    (condition_key, overlay_json, updated_utc)
                VALUES (@key, @json::jsonb, NOW())
                ON CONFLICT (condition_key) DO UPDATE SET
                    overlay_json = EXCLUDED.overlay_json,
                    updated_utc  = EXCLUDED.updated_utc;", conn);
            cmd.Parameters.AddWithValue("key", conditionKey);
            cmd.Parameters.Add(new NpgsqlParameter("json", NpgsqlDbType.Text)
            { Value = JsonSerializer.Serialize(overlay, JsonOpts) });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AdaptiveState] UpsertRetrospectiveOverlay failed for {Key}", conditionKey);
        }
    }

    public async Task DeleteRetrospectiveOverlayAsync(string conditionKey, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = new NpgsqlCommand(@"
                DELETE FROM ""ETH"".adaptive_retrospective_overlays
                WHERE condition_key = @key;", conn);
            cmd.Parameters.AddWithValue("key", conditionKey);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AdaptiveState] DeleteRetrospectiveOverlay failed for {Key}", conditionKey);
        }
    }
}
