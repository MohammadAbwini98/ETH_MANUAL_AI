using System.Text.Json;
using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EthSignal.Infrastructure.Engine;

public sealed class BlockedSignalHistoryService : IBlockedSignalHistoryService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _connectionString;
    private readonly ICandleRepository _candleRepository;
    private readonly ILogger<BlockedSignalHistoryService> _logger;

    public BlockedSignalHistoryService(
        string connectionString,
        ICandleRepository candleRepository,
        ILogger<BlockedSignalHistoryService> logger)
    {
        _connectionString = connectionString;
        _candleRepository = candleRepository;
        _logger = logger;
    }

    public async Task<BlockedSignalHistoryPage> GetHistoryAsync(
        string symbol,
        int pageSize,
        int offset,
        CancellationToken ct = default)
    {
        var rows = await GetBlockedDecisionRowsAsync(symbol, pageSize, offset, ct);
        var total = await GetBlockedDecisionCountAsync(symbol, ct);
        var items = await BuildBlockedSignalsAsync(rows, ct);

        IReadOnlyList<BlockedSignalWithOutcome> statsItems = items;
        if (total > rows.Count || offset > 0)
        {
            var statRows = await GetBlockedDecisionRowsAsync(symbol, total, 0, ct);
            statsItems = await BuildBlockedSignalsAsync(statRows, ct);
        }

        var stats = OutcomeEvaluator.ComputeStats(statsItems.Select(i => i.Outcome).ToList());

        return new BlockedSignalHistoryPage
        {
            Signals = items,
            Stats = stats,
            Total = total,
            Page = (offset / Math.Max(1, pageSize)) + 1,
            PageSize = pageSize
        };
    }

    private async Task<List<BlockedSignalWithOutcome>> BuildBlockedSignalsAsync(
        IReadOnlyList<BlockedDecisionRow> rows,
        CancellationToken ct)
    {
        var items = new List<BlockedSignalWithOutcome>(rows.Count);
        foreach (var row in rows)
        {
            var item = await BuildBlockedSignalAsync(row, ct);
            if (item != null)
                items.Add(item);
        }

        return items;
    }

    private async Task<List<BlockedDecisionRow>> GetBlockedDecisionRowsAsync(
        string symbol,
        int pageSize,
        int offset,
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
              AND outcome_category = 'OPERATIONAL_BLOCKED'
              AND COALESCE(candidate_direction, decision_type) IN ('BUY', 'SELL')
            ORDER BY decision_time_utc DESC
            LIMIT @limit OFFSET @offset;", conn);
        cmd.Parameters.AddWithValue("symbol", symbol);
        cmd.Parameters.AddWithValue("limit", pageSize);
        cmd.Parameters.AddWithValue("offset", offset);

        var rows = new List<BlockedDecisionRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new BlockedDecisionRow
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

    private async Task<int> GetBlockedDecisionCountAsync(string symbol, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT COUNT(*)::INT
            FROM ""ETH"".signal_decision_audit
            WHERE symbol = @symbol
              AND outcome_category = 'OPERATIONAL_BLOCKED'
              AND COALESCE(candidate_direction, decision_type) IN ('BUY', 'SELL');", conn);
        cmd.Parameters.AddWithValue("symbol", symbol);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private async Task<BlockedSignalWithOutcome?> BuildBlockedSignalAsync(BlockedDecisionRow row, CancellationToken ct)
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
        var blockReason = row.FinalBlockReason ?? "Blocked decision";

        var signal = new BlockedSignalRecommendation
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
            BlockReason = blockReason,
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
        Timeframe? intrabarTimeframe = null;
        if (timeframe != Timeframe.M1 && futureCandles.Count > 0)
        {
            intrabarTimeframe = Timeframe.M1;
            intrabarCandles = await _candleRepository.GetClosedCandlesInRangeAsync(
                intrabarTimeframe,
                row.Symbol,
                futureCandles[0].OpenTime,
                futureCandles[^1].OpenTime.Add(timeframe.Duration),
                ct);
        }

        var outcome = OutcomeEvaluator.Evaluate(
            new SignalRecommendation
            {
                SignalId = signal.SignalId,
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
                Status = SignalStatus.OPEN
            },
            futureCandles,
            parameters,
            timeframe,
            intrabarCandles,
            intrabarTimeframe);

        return new BlockedSignalWithOutcome
        {
            Signal = signal,
            Outcome = outcome
        };
    }

    private static StrategyParameters ParseParameters(string? json, string? parameterSetId)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<StrategyParameters>(json, JsonOpts);
                if (parsed != null)
                    return parsed;
            }
            catch
            {
                // Fall back to defaults below.
            }
        }

        return StrategyParameters.Default with
        {
            StrategyVersion = string.IsNullOrWhiteSpace(parameterSetId)
                ? StrategyParameters.Default.StrategyVersion
                : parameterSetId
        };
    }

    private static Dictionary<string, decimal> ParseIndicators(string json)
        => JsonSerializer.Deserialize<Dictionary<string, decimal>>(json, JsonOpts) ?? [];

    private static IReadOnlyList<string> ParseReasons(string json)
        => JsonSerializer.Deserialize<List<string>>(json, JsonOpts) ?? [];

    private static Regime ParseRegime(string? raw)
        => Enum.TryParse<Regime>(raw, out var regime) ? regime : Regime.NEUTRAL;

    private static DecisionOrigin ParseOrigin(string? raw)
        => Enum.TryParse<DecisionOrigin>(raw, out var origin) ? origin : DecisionOrigin.CLOSED_BAR;

    private static SourceMode ParseSourceMode(string raw)
        => Enum.TryParse<SourceMode>(raw, out var sourceMode) ? sourceMode : SourceMode.LIVE;

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
            ExitModel = "BLOCKED_FALLBACK",
            Explanation = "Blocked recommendation reconstructed with ATR fallback exit levels.",
            RejectReason = null
        };
    }

    private sealed record BlockedDecisionRow
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
