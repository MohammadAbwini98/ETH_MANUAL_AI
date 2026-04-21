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

    public async Task<IReadOnlyList<AdaptiveTimeframeProfileState>> LoadTimeframeProfileStatesAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT symbol, timeframe, strategy_version, profile_bucket,
                   adaptive_enabled, retrospective_enabled, has_retrospective_overlay,
                   effective_intensity, current_condition_class, current_coarse_condition_key,
                   overlay_diffs_json, retrospective_overlay_json,
                   base_parameters_json, effective_parameters_json,
                   base_parameter_hash, effective_parameter_hash,
                   last_evaluated_bar_utc, last_changed_utc, change_version
            FROM ""ETH"".adaptive_timeframe_profile_states
            ORDER BY symbol, timeframe;", conn);

        var results = new List<AdaptiveTimeframeProfileState>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var retroOverlay = r.IsDBNull(11)
                ? null
                : JsonSerializer.Deserialize<ParameterOverlay>(r.GetString(11), JsonOpts);
            var baseParameters = JsonSerializer.Deserialize<StrategyParameters>(r.GetString(12), JsonOpts)
                ?? StrategyParameters.Default;
            var effectiveParameters = JsonSerializer.Deserialize<StrategyParameters>(r.GetString(13), JsonOpts)
                ?? StrategyParameters.Default;

            results.Add(new AdaptiveTimeframeProfileState
            {
                Symbol = r.GetString(0),
                Timeframe = r.GetString(1),
                StrategyVersion = r.GetString(2),
                ProfileBucket = Enum.Parse<TimeframeProfileBucket>(r.GetString(3), ignoreCase: true),
                AdaptiveEnabled = r.GetBoolean(4),
                RetrospectiveEnabled = r.GetBoolean(5),
                HasRetrospectiveOverlay = r.GetBoolean(6),
                EffectiveIntensity = r.GetDecimal(7),
                CurrentConditionClass = r.IsDBNull(8) ? null : r.GetString(8),
                CurrentCoarseConditionKey = r.IsDBNull(9) ? null : r.GetString(9),
                OverlayDiffsJson = r.IsDBNull(10) ? null : r.GetString(10),
                RetrospectiveOverlay = retroOverlay,
                BaseParameters = baseParameters,
                EffectiveParameters = effectiveParameters,
                BaseParameterHash = r.GetString(14),
                EffectiveParameterHash = r.GetString(15),
                LastEvaluatedBarUtc = r.GetFieldValue<DateTimeOffset>(16),
                LastChangedUtc = r.GetFieldValue<DateTimeOffset>(17),
                ChangeVersion = r.GetInt64(18)
            });
        }

        return results;
    }

    public async Task UpsertTimeframeProfileStateAsync(AdaptiveTimeframeProfileState state, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO ""ETH"".adaptive_timeframe_profile_states
                    (symbol, timeframe, strategy_version, profile_bucket,
                     adaptive_enabled, retrospective_enabled, has_retrospective_overlay,
                     effective_intensity, current_condition_class, current_coarse_condition_key,
                     overlay_diffs_json, retrospective_overlay_json,
                     base_parameters_json, effective_parameters_json,
                     base_parameter_hash, effective_parameter_hash,
                     last_evaluated_bar_utc, last_changed_utc, change_version, updated_at_utc)
                VALUES
                    (@symbol, @timeframe, @strategyVersion, @profileBucket,
                     @adaptiveEnabled, @retrospectiveEnabled, @hasRetroOverlay,
                     @effectiveIntensity, @conditionClass, @coarseConditionKey,
                     @overlayDiffs::jsonb, @retrospectiveOverlay::jsonb,
                     @baseParameters::jsonb, @effectiveParameters::jsonb,
                     @baseHash, @effectiveHash,
                     @lastEvaluatedBarUtc, @lastChangedUtc, @changeVersion, NOW())
                ON CONFLICT (symbol, timeframe) DO UPDATE SET
                    strategy_version = EXCLUDED.strategy_version,
                    profile_bucket = EXCLUDED.profile_bucket,
                    adaptive_enabled = EXCLUDED.adaptive_enabled,
                    retrospective_enabled = EXCLUDED.retrospective_enabled,
                    has_retrospective_overlay = EXCLUDED.has_retrospective_overlay,
                    effective_intensity = EXCLUDED.effective_intensity,
                    current_condition_class = EXCLUDED.current_condition_class,
                    current_coarse_condition_key = EXCLUDED.current_coarse_condition_key,
                    overlay_diffs_json = EXCLUDED.overlay_diffs_json,
                    retrospective_overlay_json = EXCLUDED.retrospective_overlay_json,
                    base_parameters_json = EXCLUDED.base_parameters_json,
                    effective_parameters_json = EXCLUDED.effective_parameters_json,
                    base_parameter_hash = EXCLUDED.base_parameter_hash,
                    effective_parameter_hash = EXCLUDED.effective_parameter_hash,
                    last_evaluated_bar_utc = EXCLUDED.last_evaluated_bar_utc,
                    last_changed_utc = EXCLUDED.last_changed_utc,
                    change_version = EXCLUDED.change_version,
                    updated_at_utc = NOW();", conn);

            BindTimeframeStateParameters(cmd, state);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AdaptiveState] UpsertTimeframeProfileState failed for {Symbol}/{Timeframe}",
                state.Symbol, state.Timeframe);
        }
    }

    public async Task AppendTimeframeProfileChangeAsync(AdaptiveTimeframeProfileChange change, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO ""ETH"".adaptive_timeframe_profile_changes
                    (symbol, timeframe, strategy_version, profile_bucket, change_reason,
                     previous_condition_class, current_condition_class,
                     previous_parameter_hash, current_parameter_hash,
                     adaptive_enabled, retrospective_enabled, has_retrospective_overlay,
                     effective_intensity, overlay_diffs_json, retrospective_overlay_json,
                     base_parameters_json, effective_parameters_json,
                     bar_time_utc, changed_at_utc, change_version)
                VALUES
                    (@symbol, @timeframe, @strategyVersion, @profileBucket, @changeReason,
                     @previousConditionClass, @currentConditionClass,
                     @previousParameterHash, @currentParameterHash,
                     @adaptiveEnabled, @retrospectiveEnabled, @hasRetroOverlay,
                     @effectiveIntensity, @overlayDiffs::jsonb, @retrospectiveOverlay::jsonb,
                     @baseParameters::jsonb, @effectiveParameters::jsonb,
                     @barTimeUtc, @changedAtUtc, @changeVersion);", conn);

            cmd.Parameters.AddWithValue("symbol", change.Symbol);
            cmd.Parameters.AddWithValue("timeframe", change.Timeframe);
            cmd.Parameters.AddWithValue("strategyVersion", change.StrategyVersion);
            cmd.Parameters.AddWithValue("profileBucket", change.ProfileBucket.ToString());
            cmd.Parameters.AddWithValue("changeReason", change.ChangeReason);
            cmd.Parameters.AddWithValue("previousConditionClass", (object?)change.PreviousConditionClass ?? DBNull.Value);
            cmd.Parameters.AddWithValue("currentConditionClass", (object?)change.CurrentConditionClass ?? DBNull.Value);
            cmd.Parameters.AddWithValue("previousParameterHash", (object?)change.PreviousParameterHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("currentParameterHash", change.CurrentParameterHash);
            cmd.Parameters.AddWithValue("adaptiveEnabled", change.AdaptiveEnabled);
            cmd.Parameters.AddWithValue("retrospectiveEnabled", change.RetrospectiveEnabled);
            cmd.Parameters.AddWithValue("hasRetroOverlay", change.HasRetrospectiveOverlay);
            cmd.Parameters.AddWithValue("effectiveIntensity", change.EffectiveIntensity);
            cmd.Parameters.Add(new NpgsqlParameter("overlayDiffs", NpgsqlDbType.Text) { Value = (object?)change.OverlayDiffsJson ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("retrospectiveOverlay", NpgsqlDbType.Text)
            {
                Value = change.RetrospectiveOverlay is null
                    ? DBNull.Value
                    : JsonSerializer.Serialize(change.RetrospectiveOverlay, JsonOpts)
            });
            cmd.Parameters.Add(new NpgsqlParameter("baseParameters", NpgsqlDbType.Text)
            {
                Value = JsonSerializer.Serialize(change.BaseParameters, JsonOpts)
            });
            cmd.Parameters.Add(new NpgsqlParameter("effectiveParameters", NpgsqlDbType.Text)
            {
                Value = JsonSerializer.Serialize(change.EffectiveParameters, JsonOpts)
            });
            cmd.Parameters.AddWithValue("barTimeUtc", change.BarTimeUtc);
            cmd.Parameters.AddWithValue("changedAtUtc", change.ChangedAtUtc);
            cmd.Parameters.AddWithValue("changeVersion", change.ChangeVersion);

            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AdaptiveState] AppendTimeframeProfileChange failed for {Symbol}/{Timeframe}",
                change.Symbol, change.Timeframe);
        }
    }

    public async Task<IReadOnlyList<AdaptiveTimeframeProfileChange>> LoadRecentTimeframeProfileChangesAsync(int limit, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT id, symbol, timeframe, strategy_version, profile_bucket, change_reason,
                   previous_condition_class, current_condition_class,
                   previous_parameter_hash, current_parameter_hash,
                   adaptive_enabled, retrospective_enabled, has_retrospective_overlay,
                   effective_intensity, overlay_diffs_json, retrospective_overlay_json,
                   base_parameters_json, effective_parameters_json,
                   bar_time_utc, changed_at_utc, change_version
            FROM ""ETH"".adaptive_timeframe_profile_changes
            ORDER BY changed_at_utc DESC, id DESC
            LIMIT @limit;", conn);
        cmd.Parameters.AddWithValue("limit", Math.Max(1, limit));

        var results = new List<AdaptiveTimeframeProfileChange>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var retroOverlay = r.IsDBNull(15)
                ? null
                : JsonSerializer.Deserialize<ParameterOverlay>(r.GetString(15), JsonOpts);
            var baseParameters = JsonSerializer.Deserialize<StrategyParameters>(r.GetString(16), JsonOpts)
                ?? StrategyParameters.Default;
            var effectiveParameters = JsonSerializer.Deserialize<StrategyParameters>(r.GetString(17), JsonOpts)
                ?? StrategyParameters.Default;

            results.Add(new AdaptiveTimeframeProfileChange
            {
                Id = r.GetInt64(0),
                Symbol = r.GetString(1),
                Timeframe = r.GetString(2),
                StrategyVersion = r.GetString(3),
                ProfileBucket = Enum.Parse<TimeframeProfileBucket>(r.GetString(4), ignoreCase: true),
                ChangeReason = r.GetString(5),
                PreviousConditionClass = r.IsDBNull(6) ? null : r.GetString(6),
                CurrentConditionClass = r.IsDBNull(7) ? null : r.GetString(7),
                PreviousParameterHash = r.IsDBNull(8) ? null : r.GetString(8),
                CurrentParameterHash = r.GetString(9),
                AdaptiveEnabled = r.GetBoolean(10),
                RetrospectiveEnabled = r.GetBoolean(11),
                HasRetrospectiveOverlay = r.GetBoolean(12),
                EffectiveIntensity = r.GetDecimal(13),
                OverlayDiffsJson = r.IsDBNull(14) ? null : r.GetString(14),
                RetrospectiveOverlay = retroOverlay,
                BaseParameters = baseParameters,
                EffectiveParameters = effectiveParameters,
                BarTimeUtc = r.GetFieldValue<DateTimeOffset>(18),
                ChangedAtUtc = r.GetFieldValue<DateTimeOffset>(19),
                ChangeVersion = r.GetInt64(20)
            });
        }

        return results;
    }

    private static void BindTimeframeStateParameters(NpgsqlCommand cmd, AdaptiveTimeframeProfileState state)
    {
        cmd.Parameters.AddWithValue("symbol", state.Symbol);
        cmd.Parameters.AddWithValue("timeframe", state.Timeframe);
        cmd.Parameters.AddWithValue("strategyVersion", state.StrategyVersion);
        cmd.Parameters.AddWithValue("profileBucket", state.ProfileBucket.ToString());
        cmd.Parameters.AddWithValue("adaptiveEnabled", state.AdaptiveEnabled);
        cmd.Parameters.AddWithValue("retrospectiveEnabled", state.RetrospectiveEnabled);
        cmd.Parameters.AddWithValue("hasRetroOverlay", state.HasRetrospectiveOverlay);
        cmd.Parameters.AddWithValue("effectiveIntensity", state.EffectiveIntensity);
        cmd.Parameters.AddWithValue("conditionClass", (object?)state.CurrentConditionClass ?? DBNull.Value);
        cmd.Parameters.AddWithValue("coarseConditionKey", (object?)state.CurrentCoarseConditionKey ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter("overlayDiffs", NpgsqlDbType.Text) { Value = (object?)state.OverlayDiffsJson ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("retrospectiveOverlay", NpgsqlDbType.Text)
        {
            Value = state.RetrospectiveOverlay is null
                ? DBNull.Value
                : JsonSerializer.Serialize(state.RetrospectiveOverlay, JsonOpts)
        });
        cmd.Parameters.Add(new NpgsqlParameter("baseParameters", NpgsqlDbType.Text)
        {
            Value = JsonSerializer.Serialize(state.BaseParameters, JsonOpts)
        });
        cmd.Parameters.Add(new NpgsqlParameter("effectiveParameters", NpgsqlDbType.Text)
        {
            Value = JsonSerializer.Serialize(state.EffectiveParameters, JsonOpts)
        });
        cmd.Parameters.AddWithValue("baseHash", state.BaseParameterHash);
        cmd.Parameters.AddWithValue("effectiveHash", state.EffectiveParameterHash);
        cmd.Parameters.AddWithValue("lastEvaluatedBarUtc", state.LastEvaluatedBarUtc);
        cmd.Parameters.AddWithValue("lastChangedUtc", state.LastChangedUtc);
        cmd.Parameters.AddWithValue("changeVersion", state.ChangeVersion);
    }
}
