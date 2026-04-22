using System.Text.Json;
using EthSignal.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace EthSignal.Infrastructure.Db;

/// <summary>TF-2: PostgreSQL implementation of decision audit persistence.</summary>
public sealed class DecisionAuditRepository : IDecisionAuditRepository
{
    private readonly string _connectionString;
    private readonly ILogger<DecisionAuditRepository> _logger;

    public DecisionAuditRepository(string connectionString,
        ILogger<DecisionAuditRepository>? logger = null)
    {
        _connectionString = connectionString;
        _logger = logger ?? NullLogger<DecisionAuditRepository>.Instance;
    }

    public async Task<bool> InsertDecisionAsync(SignalDecision decision, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO ""ETH"".signal_decision_audit
                (id, symbol, decision_time_utc, bar_time_utc, timeframe,
                 decision_type, outcome_category, regime, regime_bar_time_utc,
                 parameter_set_id, confidence_score, blended_confidence, effective_threshold, reason_codes_json,
                 reason_details_json, indicators_json, source_mode,
                 market_condition_class, adapted_parameters_json,
                 lifecycle_state, final_block_reason, origin,
                 evaluation_id, effective_runtime_parameters_json, candidate_direction,
                 created_at_utc)
            VALUES (@id, @symbol, @decisionTime, @barTime, @tf,
                    @decisionType, @outcomeCategory, @regime, @regimeBarTime,
                    @paramSetId, @score, @blendedConfidence, @effectiveThreshold, @reasonCodes,
                    @reasonDetails, @indicators, @sourceMode,
                    @mcc, @adaptedJson,
                    @lifecycleState, @finalBlockReason, @origin,
                    @evaluationId, @effectiveParams, @candidateDirection,
                    NOW())
            ON CONFLICT (symbol, timeframe, bar_time_utc, source_mode) DO UPDATE SET
                    decision_time_utc = EXCLUDED.decision_time_utc,
                    decision_type = EXCLUDED.decision_type,
                    outcome_category = EXCLUDED.outcome_category,
                    regime = EXCLUDED.regime,
                    regime_bar_time_utc = EXCLUDED.regime_bar_time_utc,
                    parameter_set_id = EXCLUDED.parameter_set_id,
                    confidence_score = EXCLUDED.confidence_score,
                    blended_confidence = EXCLUDED.blended_confidence,
                    effective_threshold = EXCLUDED.effective_threshold,
                    reason_codes_json = EXCLUDED.reason_codes_json,
                    reason_details_json = EXCLUDED.reason_details_json,
                    indicators_json = EXCLUDED.indicators_json,
                    market_condition_class = EXCLUDED.market_condition_class,
                    lifecycle_state = EXCLUDED.lifecycle_state,
                    final_block_reason = EXCLUDED.final_block_reason,
                    origin = EXCLUDED.origin,
                    evaluation_id = EXCLUDED.evaluation_id,
                    effective_runtime_parameters_json = EXCLUDED.effective_runtime_parameters_json,
                    adapted_parameters_json = EXCLUDED.adapted_parameters_json,
                    candidate_direction = EXCLUDED.candidate_direction;", conn);

        cmd.Parameters.AddWithValue("id", decision.DecisionId);
        cmd.Parameters.AddWithValue("symbol", decision.Symbol);
        cmd.Parameters.AddWithValue("decisionTime", decision.DecisionTimeUtc);
        cmd.Parameters.AddWithValue("barTime", decision.BarTimeUtc);
        cmd.Parameters.AddWithValue("tf", decision.Timeframe);
        cmd.Parameters.AddWithValue("decisionType", decision.DecisionType.ToString());
        cmd.Parameters.AddWithValue("outcomeCategory", decision.OutcomeCategory.ToString());
        cmd.Parameters.AddWithValue("regime", (object?)decision.UsedRegime?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("regimeBarTime", (object?)decision.UsedRegimeTimestamp ?? DBNull.Value);
        cmd.Parameters.AddWithValue("paramSetId", (object?)decision.ParameterSetId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("score", decision.ConfidenceScore);
        cmd.Parameters.AddWithValue("blendedConfidence", (object?)decision.BlendedConfidence ?? DBNull.Value);
        cmd.Parameters.AddWithValue("effectiveThreshold", (object?)decision.EffectiveThreshold ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter("reasonCodes", NpgsqlDbType.Jsonb)
        { Value = decision.ReasonCodesJson });
        cmd.Parameters.Add(new NpgsqlParameter("reasonDetails", NpgsqlDbType.Jsonb)
        { Value = decision.ReasonDetailsJson });
        cmd.Parameters.Add(new NpgsqlParameter("indicators", NpgsqlDbType.Jsonb)
        { Value = decision.IndicatorsJson });
        cmd.Parameters.AddWithValue("sourceMode", decision.SourceMode.ToString());
        cmd.Parameters.AddWithValue("mcc", (object?)decision.MarketConditionClass ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter("adaptedJson", NpgsqlDbType.Jsonb)
        { Value = (object?)decision.AdaptedParametersJson ?? DBNull.Value });
        cmd.Parameters.AddWithValue("lifecycleState", decision.LifecycleState.ToString());
        cmd.Parameters.AddWithValue("finalBlockReason", (object?)decision.FinalBlockReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("origin", decision.Origin.ToString());
        cmd.Parameters.AddWithValue("evaluationId", decision.EvaluationId);
        cmd.Parameters.Add(new NpgsqlParameter("effectiveParams", NpgsqlDbType.Jsonb)
        { Value = (object?)decision.EffectiveRuntimeParametersJson ?? DBNull.Value });
        cmd.Parameters.AddWithValue("candidateDirection", (object?)decision.CandidateDirection?.ToString() ?? DBNull.Value);

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows >= 1;
    }

    public async Task<bool> ExistsForBarAsync(string symbol, string timeframe, DateTimeOffset barTimeUtc,
        SourceMode sourceMode, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT 1 FROM ""ETH"".signal_decision_audit
            WHERE symbol = @s AND timeframe = @tf AND bar_time_utc = @bar AND source_mode = @mode
            LIMIT 1;", conn);
        cmd.Parameters.AddWithValue("s", symbol);
        cmd.Parameters.AddWithValue("tf", timeframe);
        cmd.Parameters.AddWithValue("bar", barTimeUtc);
        cmd.Parameters.AddWithValue("mode", sourceMode.ToString());

        return await cmd.ExecuteScalarAsync(ct) != null;
    }

    public async Task<SignalDecision?> GetLatestDecisionAsync(string symbol, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT id, symbol, decision_time_utc, bar_time_utc, timeframe,
                   decision_type, outcome_category, regime, regime_bar_time_utc,
                   parameter_set_id, confidence_score, blended_confidence, effective_threshold, reason_codes_json,
                   reason_details_json, indicators_json, source_mode,
                   market_condition_class, adapted_parameters_json,
                   lifecycle_state, final_block_reason, origin,
                   evaluation_id, effective_runtime_parameters_json, candidate_direction
            FROM ""ETH"".signal_decision_audit
            WHERE symbol = @s ORDER BY decision_time_utc DESC LIMIT 1;", conn);
        cmd.Parameters.AddWithValue("s", symbol);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return ReadDecision(r);
    }

    public async Task<SignalDecision?> GetDecisionByEvaluationIdAsync(Guid evaluationId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT id, symbol, decision_time_utc, bar_time_utc, timeframe,
                   decision_type, outcome_category, regime, regime_bar_time_utc,
                   parameter_set_id, confidence_score, blended_confidence, effective_threshold, reason_codes_json,
                   reason_details_json, indicators_json, source_mode,
                   market_condition_class, adapted_parameters_json,
                   lifecycle_state, final_block_reason, origin,
                   evaluation_id, effective_runtime_parameters_json, candidate_direction
            FROM ""ETH"".signal_decision_audit
            WHERE evaluation_id = @evaluationId
            ORDER BY decision_time_utc DESC
            LIMIT 1;", conn);
        cmd.Parameters.AddWithValue("evaluationId", evaluationId);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return ReadDecision(r);
    }

    public async Task<IReadOnlyList<SignalDecision>> GetDecisionsAsync(string symbol, DateTimeOffset from,
        DateTimeOffset to, int limit = 1000, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT id, symbol, decision_time_utc, bar_time_utc, timeframe,
                   decision_type, outcome_category, regime, regime_bar_time_utc,
                   parameter_set_id, confidence_score, blended_confidence, effective_threshold, reason_codes_json,
                   reason_details_json, indicators_json, source_mode,
                   market_condition_class, adapted_parameters_json,
                   lifecycle_state, final_block_reason, origin,
                   evaluation_id, effective_runtime_parameters_json, candidate_direction
            FROM ""ETH"".signal_decision_audit
            WHERE symbol = @s AND decision_time_utc >= @from AND decision_time_utc < @to
            ORDER BY decision_time_utc DESC LIMIT @lim;", conn);
        cmd.Parameters.AddWithValue("s", symbol);
        cmd.Parameters.AddWithValue("from", from);
        cmd.Parameters.AddWithValue("to", to);
        cmd.Parameters.AddWithValue("lim", limit);

        var results = new List<SignalDecision>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            results.Add(ReadDecision(r));
        return results;
    }

    public async Task<DecisionSummary> GetSummaryAsync(string symbol, DateTimeOffset from,
        DateTimeOffset to, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT
                COUNT(*) AS total,
                COUNT(*) FILTER (WHERE decision_type = 'BUY' OR decision_type = 'SELL') AS signal_count,
                COUNT(*) FILTER (WHERE decision_type = 'BUY') AS long_count,
                COUNT(*) FILTER (WHERE decision_type = 'SELL') AS short_count,
                COUNT(*) FILTER (WHERE decision_type = 'NO_TRADE') AS no_trade_count,
                COUNT(*) FILTER (WHERE outcome_category = 'STRATEGY_NO_TRADE') AS strategy_nt,
                COUNT(*) FILTER (WHERE outcome_category = 'OPERATIONAL_BLOCKED') AS op_blocked,
                COUNT(*) FILTER (WHERE outcome_category = 'CONTEXT_NOT_READY') AS ctx_nr,
                MAX(decision_time_utc) FILTER (WHERE decision_type IN ('BUY','SELL')) AS last_signal_time,
                MAX(decision_time_utc) AS last_eval_time
            FROM ""ETH"".signal_decision_audit
            WHERE symbol = @s AND decision_time_utc >= @from AND decision_time_utc < @to;", conn);
        cmd.Parameters.AddWithValue("s", symbol);
        cmd.Parameters.AddWithValue("from", from);
        cmd.Parameters.AddWithValue("to", to);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            return new DecisionSummary();

        var result = new DecisionSummary
        {
            TotalDecisions = r.GetInt32(0),
            LongCount = r.GetInt32(2),
            ShortCount = r.GetInt32(3),
            NoTradeCount = r.GetInt32(4),
            StrategyNoTradeCount = r.GetInt32(5),
            OperationalBlockedCount = r.GetInt32(6),
            ContextNotReadyCount = r.GetInt32(7),
            LastSignalTime = r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8),
            LastEvaluationTime = r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9)
        };

        // Close the first reader before running the next command on the same connection
        await r.CloseAsync();

        // Fetch top reject reasons (reason_codes_json is JSONB array)
        await using var cmd2 = new NpgsqlCommand(@"
            SELECT elem::text AS reason, COUNT(*) AS cnt
            FROM ""ETH"".signal_decision_audit,
                 jsonb_array_elements_text(reason_codes_json) AS elem
            WHERE symbol = @s AND decision_time_utc >= @from AND decision_time_utc < @to
              AND jsonb_array_length(reason_codes_json) > 0
            GROUP BY reason ORDER BY cnt DESC LIMIT 10;", conn);
        cmd2.Parameters.AddWithValue("s", symbol);
        cmd2.Parameters.AddWithValue("from", from);
        cmd2.Parameters.AddWithValue("to", to);

        var topReasons = new List<(string Reason, int Count)>();
        await using var r2 = await cmd2.ExecuteReaderAsync(ct);
        while (await r2.ReadAsync(ct))
            topReasons.Add((r2.GetString(0).Trim(), r2.GetInt32(1)));

        return result with { TopRejectReasons = topReasons };
    }

    public async Task<IReadOnlyList<DecisionMlBackfillCandidate>> GetBlockedMlBackfillCandidatesAsync(
        string symbol,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT
                b.evaluation_id,
                b.symbol,
                b.timeframe,
                b.signal_time_utc,
                d.decision_time_utc,
                d.bar_time_utc,
                d.decision_type,
                d.candidate_direction,
                d.regime,
                d.confidence_score,
                d.parameter_set_id,
                d.effective_runtime_parameters_json,
                d.indicators_json
            FROM ""ETH"".blocked_signal_outcomes b
            JOIN ""ETH"".signal_decision_audit d
              ON d.evaluation_id = b.evaluation_id
            LEFT JOIN ""ETH"".ml_feature_snapshots f
              ON f.evaluation_id = b.evaluation_id
            WHERE b.symbol = @symbol
              AND f.evaluation_id IS NULL
              AND COALESCE(d.candidate_direction, d.decision_type) IN ('BUY', 'SELL')
            ORDER BY b.signal_time_utc ASC;", conn);
        cmd.Parameters.AddWithValue("symbol", symbol);

        var rows = new List<DecisionMlBackfillCandidate>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new DecisionMlBackfillCandidate
            {
                EvaluationId = reader.GetGuid(0),
                Symbol = reader.GetString(1),
                Timeframe = reader.GetString(2),
                SignalTimeUtc = reader.GetFieldValue<DateTimeOffset>(3),
                DecisionTimeUtc = reader.GetFieldValue<DateTimeOffset>(4),
                BarTimeUtc = reader.GetFieldValue<DateTimeOffset>(5),
                DecisionTypeRaw = reader.GetString(6),
                CandidateDirectionRaw = reader.IsDBNull(7) ? null : reader.GetString(7),
                RegimeRaw = reader.IsDBNull(8) ? null : reader.GetString(8),
                ConfidenceScore = reader.GetInt32(9),
                ParameterSetId = reader.IsDBNull(10) ? null : reader.GetString(10),
                EffectiveRuntimeParametersJson = reader.IsDBNull(11) ? null : reader.GetString(11),
                IndicatorsJson = reader.GetString(12)
            });
        }

        return rows;
    }

    private static SignalDecision ReadDecision(NpgsqlDataReader r)
    {
        var reasonCodesRaw = r.GetString(13);
        var reasonDetailsRaw = r.GetString(14);
        var indicatorsRaw = r.GetString(15);
        var regimeStr = r.IsDBNull(7) ? null : r.GetString(7);

        return new SignalDecision
        {
            DecisionId = r.GetGuid(0),
            Symbol = r.GetString(1),
            DecisionTimeUtc = r.GetFieldValue<DateTimeOffset>(2),
            BarTimeUtc = r.GetFieldValue<DateTimeOffset>(3),
            Timeframe = r.GetString(4),
            DecisionType = Enum.Parse<SignalDirection>(r.GetString(5)),
            OutcomeCategory = Enum.Parse<OutcomeCategory>(r.GetString(6)),
            UsedRegime = regimeStr != null ? Enum.Parse<Regime>(regimeStr) : null,
            UsedRegimeTimestamp = r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8),
            ParameterSetId = r.IsDBNull(9) ? null : r.GetString(9),
            ConfidenceScore = r.GetInt32(10),
            BlendedConfidence = r.IsDBNull(11) ? null : r.GetInt32(11),
            EffectiveThreshold = r.IsDBNull(12) ? null : r.GetInt32(12),
            ReasonCodes = ParseReasonCodes(reasonCodesRaw),
            ReasonDetails = JsonSerializer.Deserialize<List<string>>(reasonDetailsRaw) ?? [],
            IndicatorSnapshot = JsonSerializer.Deserialize<Dictionary<string, decimal>>(indicatorsRaw)
                                ?? new Dictionary<string, decimal>(),
            SourceMode = Enum.Parse<SourceMode>(r.GetString(16)),
            MarketConditionClass = r.IsDBNull(17) ? null : r.GetString(17),
            AdaptedParametersJson = r.IsDBNull(18) ? null : r.GetString(18),
            LifecycleState = r.IsDBNull(19) ? SignalLifecycleState.EVALUATED : Enum.Parse<SignalLifecycleState>(r.GetString(19)),
            FinalBlockReason = r.IsDBNull(20) ? null : r.GetString(20),
            Origin = r.IsDBNull(21) ? DecisionOrigin.CLOSED_BAR : Enum.Parse<DecisionOrigin>(r.GetString(21)),
            EvaluationId = r.IsDBNull(22) ? Guid.Empty : r.GetGuid(22),
            EffectiveRuntimeParametersJson = r.IsDBNull(23) ? null : r.GetString(23),
            CandidateDirection = r.IsDBNull(24) ? null : Enum.Parse<SignalDirection>(r.GetString(24))
        };
    }

    private static IReadOnlyList<RejectReasonCode> ParseReasonCodes(string json)
    {
        var strings = JsonSerializer.Deserialize<List<string>>(json) ?? [];
        return strings.Select(s => Enum.Parse<RejectReasonCode>(s)).ToList();
    }
}
