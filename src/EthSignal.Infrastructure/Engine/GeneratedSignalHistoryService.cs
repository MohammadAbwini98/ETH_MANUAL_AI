using System.Text.Json;
using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace EthSignal.Infrastructure.Engine;

public sealed class GeneratedSignalHistoryService : IGeneratedSignalHistoryService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _connectionString;
    private readonly ICandleRepository _candleRepository;
    private readonly ILogger<GeneratedSignalHistoryService> _logger;

    public GeneratedSignalHistoryService(
        string connectionString,
        ICandleRepository candleRepository,
        ILogger<GeneratedSignalHistoryService> logger)
    {
        _connectionString = connectionString;
        _candleRepository = candleRepository;
        _logger = logger;
    }

    public async Task<GeneratedSignalHistoryPage> GetHistoryAsync(
        string symbol,
        int pageSize,
        int offset,
        int? hours = null,
        CancellationToken ct = default)
    {
        var fromUtc = hours.HasValue
            ? DateTimeOffset.UtcNow.AddHours(-Math.Max(1, hours.Value))
            : (DateTimeOffset?)null;
        var rows = await GetGeneratedDecisionRowsAsync(symbol, pageSize, offset, fromUtc, ct);
        var total = await GetGeneratedDecisionCountAsync(symbol, fromUtc, ct);
        var items = await BuildGeneratedSignalsAsync(rows, ct);

        IReadOnlyList<GeneratedSignalWithOutcome> statsItems = items;
        if (total > rows.Count || offset > 0)
        {
            var statRows = await GetGeneratedDecisionRowsAsync(symbol, total, 0, fromUtc, ct);
            statsItems = await BuildGeneratedSignalsAsync(statRows, ct);
        }

        var stats = OutcomeEvaluator.ComputeStats(statsItems.Select(i => i.Outcome).ToList());

        return new GeneratedSignalHistoryPage
        {
            Signals = items,
            Stats = stats,
            Total = total,
            Page = (offset / Math.Max(1, pageSize)) + 1,
            PageSize = pageSize
        };
    }

    public async Task<GeneratedSignalWithOutcome?> GetBySignalIdAsync(
        string symbol,
        Guid signalId,
        CancellationToken ct = default)
    {
        var row = await GetGeneratedDecisionRowByIdAsync(symbol, signalId, ct);
        if (row == null)
            return null;
        return await BuildGeneratedSignalAsync(row, ct);
    }

    private async Task<List<GeneratedSignalWithOutcome>> BuildGeneratedSignalsAsync(
        IReadOnlyList<GeneratedDecisionRow> rows,
        CancellationToken ct)
    {
        var items = new List<GeneratedSignalWithOutcome>(rows.Count);
        foreach (var row in rows)
        {
            var item = await BuildGeneratedSignalAsync(row, ct);
            if (item != null)
                items.Add(item);
        }

        return items;
    }

    private async Task<List<GeneratedDecisionRow>> GetGeneratedDecisionRowsAsync(
        string symbol,
        int pageSize,
        int offset,
        DateTimeOffset? fromUtc,
        CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT
                id,
                evaluation_id,
                symbol,
                decision_time_utc,
                bar_time_utc,
                timeframe,
                decision_type,
                candidate_direction,
                regime,
                parameter_set_id,
                confidence_score,
                reason_details_json,
                indicators_json,
                source_mode,
                lifecycle_state,
                final_block_reason,
                origin,
                effective_runtime_parameters_json
            FROM ""ETH"".signal_decision_audit
            WHERE symbol = @symbol
              AND decision_type IN ('BUY', 'SELL')
              AND (@fromUtc::timestamptz IS NULL OR decision_time_utc >= @fromUtc)
            ORDER BY decision_time_utc DESC
            LIMIT @limit OFFSET @offset;", conn);
        cmd.Parameters.AddWithValue("symbol", symbol);
        cmd.Parameters.AddWithValue("limit", pageSize);
        cmd.Parameters.AddWithValue("offset", offset);
        cmd.Parameters.Add(new NpgsqlParameter("fromUtc", NpgsqlDbType.TimestampTz)
        {
            Value = (object?)fromUtc ?? DBNull.Value
        });

        var rows = new List<GeneratedDecisionRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new GeneratedDecisionRow
            {
                DecisionId = reader.GetGuid(0),
                EvaluationId = reader.GetGuid(1),
                Symbol = reader.GetString(2),
                DecisionTimeUtc = reader.GetFieldValue<DateTimeOffset>(3),
                BarTimeUtc = reader.GetFieldValue<DateTimeOffset>(4),
                Timeframe = reader.GetString(5),
                DecisionType = reader.GetString(6),
                CandidateDirection = reader.IsDBNull(7) ? null : reader.GetString(7),
                Regime = reader.IsDBNull(8) ? null : reader.GetString(8),
                ParameterSetId = reader.IsDBNull(9) ? null : reader.GetString(9),
                ConfidenceScore = reader.GetInt32(10),
                ReasonDetailsJson = reader.GetString(11),
                IndicatorsJson = reader.GetString(12),
                SourceMode = reader.GetString(13),
                LifecycleState = reader.GetString(14),
                FinalBlockReason = reader.IsDBNull(15) ? null : reader.GetString(15),
                Origin = reader.IsDBNull(16) ? null : reader.GetString(16),
                EffectiveRuntimeParametersJson = reader.IsDBNull(17) ? null : reader.GetString(17)
            });
        }

        return rows;
    }

    private async Task<int> GetGeneratedDecisionCountAsync(
        string symbol,
        DateTimeOffset? fromUtc,
        CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT COUNT(*)::INT
            FROM ""ETH"".signal_decision_audit
            WHERE symbol = @symbol
              AND decision_type IN ('BUY', 'SELL')
              AND (@fromUtc::timestamptz IS NULL OR decision_time_utc >= @fromUtc);", conn);
        cmd.Parameters.AddWithValue("symbol", symbol);
        cmd.Parameters.Add(new NpgsqlParameter("fromUtc", NpgsqlDbType.TimestampTz)
        {
            Value = (object?)fromUtc ?? DBNull.Value
        });
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private async Task<GeneratedDecisionRow?> GetGeneratedDecisionRowByIdAsync(
        string symbol,
        Guid signalId,
        CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT
                id,
                evaluation_id,
                symbol,
                decision_time_utc,
                bar_time_utc,
                timeframe,
                decision_type,
                candidate_direction,
                regime,
                parameter_set_id,
                confidence_score,
                reason_details_json,
                indicators_json,
                source_mode,
                lifecycle_state,
                final_block_reason,
                origin,
                effective_runtime_parameters_json
            FROM ""ETH"".signal_decision_audit
            WHERE symbol = @symbol
              AND id = @signalId
              AND decision_type IN ('BUY', 'SELL')
            LIMIT 1;", conn);
        cmd.Parameters.AddWithValue("symbol", symbol);
        cmd.Parameters.AddWithValue("signalId", signalId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new GeneratedDecisionRow
        {
            DecisionId = reader.GetGuid(0),
            EvaluationId = reader.GetGuid(1),
            Symbol = reader.GetString(2),
            DecisionTimeUtc = reader.GetFieldValue<DateTimeOffset>(3),
            BarTimeUtc = reader.GetFieldValue<DateTimeOffset>(4),
            Timeframe = reader.GetString(5),
            DecisionType = reader.GetString(6),
            CandidateDirection = reader.IsDBNull(7) ? null : reader.GetString(7),
            Regime = reader.IsDBNull(8) ? null : reader.GetString(8),
            ParameterSetId = reader.IsDBNull(9) ? null : reader.GetString(9),
            ConfidenceScore = reader.GetInt32(10),
            ReasonDetailsJson = reader.GetString(11),
            IndicatorsJson = reader.GetString(12),
            SourceMode = reader.GetString(13),
            LifecycleState = reader.GetString(14),
            FinalBlockReason = reader.IsDBNull(15) ? null : reader.GetString(15),
            Origin = reader.IsDBNull(16) ? null : reader.GetString(16),
            EffectiveRuntimeParametersJson = reader.IsDBNull(17) ? null : reader.GetString(17)
        };
    }

    private async Task<GeneratedSignalWithOutcome?> BuildGeneratedSignalAsync(GeneratedDecisionRow row, CancellationToken ct)
    {
        SignalDirection direction;
        try
        {
            direction = Enum.Parse<SignalDirection>(row.CandidateDirection ?? row.DecisionType);
        }
        catch
        {
            return null;
        }

        var timeframe = Timeframe.ByNameOrDefault(row.Timeframe);
        var parameters = ParseParameters(row.EffectiveRuntimeParametersJson, row.ParameterSetId);
        var indicators = ParseIndicators(row.IndicatorsJson);
        var regime = ParseRegime(row.Regime);
        var signalTimeUtc = row.BarTimeUtc.Add(timeframe.Duration);
        var expiryBars = OutcomeEvaluator.GetTimeoutBars(parameters, timeframe);
        var expiryTimeUtc = signalTimeUtc.AddTicks(timeframe.Duration.Ticks * expiryBars);

        var lookbackBars = Math.Max(parameters.ExitStructureLookbackBars, 20);
        var historyFrom = row.BarTimeUtc.AddTicks(-timeframe.Duration.Ticks * lookbackBars);
        var closedHistory = await _candleRepository.GetClosedCandlesInRangeAsync(
            timeframe,
            row.Symbol,
            historyFrom,
            signalTimeUtc,
            ct);

        var closeMid = TryGetIndicator(indicators, "close_mid")
            ?? closedHistory.LastOrDefault()?.MidClose
            ?? 0m;
        var spread = TryGetIndicator(indicators, "spread") ?? 0m;
        var atr = TryGetIndicator(indicators, "atr14") ?? 0m;
        var spreadPct = closeMid > 0 ? spread / closeMid : 0m;
        var estimatedEntry = closeMid > 0
            ? RiskManager.EstimateLiveFillPrice(direction, closeMid, spreadPct, parameters.LiveEntrySlippageBufferPct)
            : 0m;

        var recentCandles = closedHistory.TakeLast(5).ToList();
        var swingExtreme = 0m;
        if (recentCandles.Count > 0)
        {
            swingExtreme = direction == SignalDirection.BUY
                ? recentCandles.Min(c => c.MidLow)
                : recentCandles.Max(c => c.MidHigh);
        }

        StructureAnalyzer.StructureLevels? structure = null;
        if (closedHistory.Count >= parameters.ExitStructureLookbackBars)
            structure = StructureAnalyzer.Analyze(closedHistory, estimatedEntry);
        else if (closedHistory.Count >= 7)
            structure = StructureAnalyzer.Analyze(closedHistory, estimatedEntry, shoulderBars: 2);

        var exitContext = new ExitEngine.ExitContext
        {
            Direction = direction,
            EntryPrice = estimatedEntry,
            Atr = atr,
            SpreadPct = spreadPct,
            ConfidenceScore = row.ConfidenceScore,
            Regime = regime,
            Timeframe = timeframe.Name,
            Structure = structure,
            SwingExtreme = swingExtreme
        };

        var exitPolicy = timeframe == Timeframe.M1 || ParseOrigin(row.Origin) == DecisionOrigin.SCALP_1M
            ? ExitEngine.BuildScalpPolicy(parameters)
            : ExitEngine.BuildPolicy(parameters);

        var exitResult = ExitEngine.Compute(exitContext, exitPolicy);
        var usedFallbackExit = false;
        if (!exitResult.Allowed)
        {
            exitResult = BuildFallbackExit(exitContext, exitPolicy);
            usedFallbackExit = true;
        }

        var riskUsd = parameters.AccountBalanceUsd * parameters.RiskPerTradePercent / 100m;
        var reasons = ParseReasons(row.ReasonDetailsJson);

        var signal = new GeneratedSignalRecommendation
        {
            SignalId = row.DecisionId,
            EvaluationId = row.EvaluationId,
            Symbol = row.Symbol,
            Timeframe = timeframe.Name,
            SignalTimeUtc = signalTimeUtc,
            DecisionTimeUtc = row.DecisionTimeUtc,
            BarTimeUtc = row.BarTimeUtc,
            Direction = direction,
            LifecycleState = Enum.Parse<SignalLifecycleState>(row.LifecycleState),
            Origin = ParseOrigin(row.Origin),
            SourceMode = ParseSourceMode(row.SourceMode),
            Regime = regime,
            StrategyVersion = parameters.StrategyVersion,
            Reasons = reasons,
            EntryPrice = estimatedEntry,
            TpPrice = exitResult.TakeProfit,
            SlPrice = exitResult.StopLoss,
            RiskPercent = parameters.RiskPerTradePercent,
            RiskUsd = riskUsd,
            ConfidenceScore = row.ConfidenceScore,
            ExpiryBars = expiryBars,
            ExpiryTimeUtc = expiryTimeUtc,
            ExitModel = exitResult.ExitModel,
            ExitExplanation = exitResult.Explanation,
            UsedFallbackExit = usedFallbackExit
        };

        var futureCandles = await _candleRepository.GetClosedCandlesAfterAsync(
            timeframe,
            row.Symbol,
            signal.SignalTimeUtc,
            expiryBars,
            ct);

        IReadOnlyList<RichCandle>? intrabarCandles = null;
        Timeframe? intrabarTf = null;
        if (timeframe != Timeframe.M1)
        {
            intrabarTf = Timeframe.M1;
            intrabarCandles = await _candleRepository.GetClosedCandlesInRangeAsync(
                intrabarTf,
                row.Symbol,
                signal.SignalTimeUtc,
                signal.ExpiryTimeUtc,
                ct);
        }

        var outcome = OutcomeEvaluator.Evaluate(
            new SignalRecommendation
            {
                SignalId = signal.SignalId,
                EvaluationId = signal.EvaluationId,
                Symbol = signal.Symbol,
                Timeframe = signal.Timeframe,
                SignalTimeUtc = signal.SignalTimeUtc,
                Direction = signal.Direction,
                EntryPrice = signal.EntryPrice,
                TpPrice = signal.TpPrice,
                SlPrice = signal.SlPrice,
                RiskPercent = signal.RiskPercent,
                RiskUsd = signal.RiskUsd,
                ConfidenceScore = signal.ConfidenceScore,
                Regime = signal.Regime,
                StrategyVersion = signal.StrategyVersion,
                Reasons = signal.Reasons,
                Tp1Price = signal.TpPrice,
                Tp2Price = signal.TpPrice,
                Tp3Price = signal.TpPrice,
                RiskRewardRatio = 0m,
                ExitModel = signal.ExitModel,
                ExitExplanation = signal.ExitExplanation
            },
            futureCandles,
            parameters,
            timeframe,
            intrabarCandles,
            intrabarTf);

        return new GeneratedSignalWithOutcome
        {
            Signal = signal,
            Outcome = outcome
        };
    }

    private static StrategyParameters ParseParameters(string? effectiveRuntimeParametersJson, string? fallbackStrategyVersion)
    {
        if (!string.IsNullOrWhiteSpace(effectiveRuntimeParametersJson))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<StrategyParameters>(effectiveRuntimeParametersJson, JsonOpts);
                if (parsed is not null)
                    return parsed;
            }
            catch
            {
                // ignored
            }
        }

        var fallback = StrategyParameters.Default;
        if (!string.IsNullOrWhiteSpace(fallbackStrategyVersion))
            fallback = fallback with { StrategyVersion = fallbackStrategyVersion };
        return fallback;
    }

    private static Dictionary<string, decimal> ParseIndicators(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, decimal>>(raw, JsonOpts) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<string> ParseReasons(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(raw, JsonOpts) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static Regime ParseRegime(string? raw)
    {
        if (!string.IsNullOrWhiteSpace(raw) && Enum.TryParse<Regime>(raw, true, out var regime))
            return regime;
        return Regime.NEUTRAL;
    }

    private static DecisionOrigin ParseOrigin(string? raw)
    {
        if (!string.IsNullOrWhiteSpace(raw) && Enum.TryParse<DecisionOrigin>(raw, true, out var origin))
            return origin;
        return DecisionOrigin.CLOSED_BAR;
    }

    private static SourceMode ParseSourceMode(string raw)
    {
        if (Enum.TryParse<SourceMode>(raw, true, out var sourceMode))
            return sourceMode;
        return SourceMode.LIVE;
    }

    private static decimal? TryGetIndicator(IReadOnlyDictionary<string, decimal> indicators, string key)
        => indicators.TryGetValue(key, out var value) ? value : null;

    private static ExitEngine.ExitResult BuildFallbackExit(ExitEngine.ExitContext ctx, ExitEngine.ExitPolicy policy)
    {
        var stopDistance = Math.Max(
            ctx.EntryPrice * policy.MinStopDistancePct,
            Math.Max(ctx.Atr * Math.Max(policy.AtrMultiplier, 1m), ctx.EntryPrice * 0.002m));
        var rewardMultiple = Math.Max(policy.DefaultRewardToRisk, policy.MinRewardToRisk);
        var tpDistance = stopDistance * rewardMultiple;

        var stopLoss = ctx.Direction == SignalDirection.BUY
            ? ctx.EntryPrice - stopDistance
            : ctx.EntryPrice + stopDistance;
        var takeProfit = ctx.Direction == SignalDirection.BUY
            ? ctx.EntryPrice + tpDistance
            : ctx.EntryPrice - tpDistance;

        return new ExitEngine.ExitResult
        {
            Allowed = true,
            StopLoss = stopLoss,
            TakeProfit = takeProfit,
            StopDistance = stopDistance,
            Tp1 = ctx.Direction == SignalDirection.BUY ? ctx.EntryPrice + stopDistance : ctx.EntryPrice - stopDistance,
            Tp2 = ctx.Direction == SignalDirection.BUY ? ctx.EntryPrice + stopDistance * 2m : ctx.EntryPrice - stopDistance * 2m,
            Tp3 = ctx.Direction == SignalDirection.BUY ? ctx.EntryPrice + stopDistance * 3m : ctx.EntryPrice - stopDistance * 3m,
            RiskRewardRatio = rewardMultiple,
            ExitModel = "GENERATED_FALLBACK",
            Explanation = "Generated recommendation reconstructed with ATR fallback exit levels.",
            RejectReason = null
        };
    }

    private sealed record GeneratedDecisionRow
    {
        public required Guid DecisionId { get; init; }
        public required Guid EvaluationId { get; init; }
        public required string Symbol { get; init; }
        public required DateTimeOffset DecisionTimeUtc { get; init; }
        public required DateTimeOffset BarTimeUtc { get; init; }
        public required string Timeframe { get; init; }
        public required string DecisionType { get; init; }
        public string? CandidateDirection { get; init; }
        public string? Regime { get; init; }
        public string? ParameterSetId { get; init; }
        public int ConfidenceScore { get; init; }
        public required string ReasonDetailsJson { get; init; }
        public required string IndicatorsJson { get; init; }
        public required string SourceMode { get; init; }
        public required string LifecycleState { get; init; }
        public string? FinalBlockReason { get; init; }
        public string? Origin { get; init; }
        public string? EffectiveRuntimeParametersJson { get; init; }
    }
}
