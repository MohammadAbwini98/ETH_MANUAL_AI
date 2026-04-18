using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using EthSignal.Infrastructure.Engine.ML;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EthSignal.Infrastructure.Engine;

/// <summary>
/// B-07 Phase 2: Historical Replay Engine.
/// Replays closed 1m candles through the exact same domain logic used by live mode.
/// Generates regime snapshots, indicator snapshots, signals, features, and outcomes.
///
/// Critical invariant: causally correct — at bar t, only bars ≤ t are visible.
/// </summary>
public sealed class HistoricalReplayService
{
    private readonly ICandleRepository _candleRepo;
    private readonly ISignalRepository _signalRepo;
    private readonly IReplayRepository _replayRepo;
    private readonly ILogger<HistoricalReplayService> _logger;

    private const int CheckpointEveryBars = 500;

    public HistoricalReplayService(
        ICandleRepository candleRepo,
        ISignalRepository signalRepo,
        IReplayRepository replayRepo,
        ILogger<HistoricalReplayService>? logger = null)
    {
        _candleRepo = candleRepo;
        _signalRepo = signalRepo;
        _replayRepo = replayRepo;
        _logger = logger ?? NullLogger<HistoricalReplayService>.Instance;
    }

    /// <summary>
    /// Run a full historical replay over a date range.
    /// Returns the generated signals and outcomes for in-memory use (e.g., optimizer).
    /// </summary>
    public async Task<ReplayResult> RunAsync(
        string symbol,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        StrategyParameters parameters,
        long? replayRunId = null,
        bool persistArtifacts = true,
        CancellationToken ct = default)
    {
        if (replayRunId.HasValue)
            await _replayRepo.UpdateRunStatusAsync(replayRunId.Value, RunStatus.Running, null, ct);

        _logger.LogInformation("Replay starting: {Symbol} {Start} to {End}", symbol, startUtc, endUtc);

        // 1) Load all closed 1m candles in the window, ordered by time
        var allCandles1m = await _candleRepo.GetClosedCandlesInRangeAsync(
            Timeframe.M1, symbol, startUtc, endUtc, ct);

        if (allCandles1m.Count == 0)
        {
            _logger.LogWarning("No closed 1m candles found in range");
            if (replayRunId.HasValue)
                await _replayRepo.UpdateRunFinishedAsync(replayRunId.Value, RunStatus.Completed, 0, 0, 0, ct);
            return new ReplayResult();
        }

        _logger.LogInformation("Loaded {Count} closed 1m candles", allCandles1m.Count);

        // 2) Initialize replay state
        var state = new ReplayState(parameters);
        var result = new ReplayResult();
        int processedCount = 0;

        try
        {
            // 3) Process each 1m candle in time order
            foreach (var candle1m in allCandles1m)
            {
                ct.ThrowIfCancellationRequested();
                processedCount++;

                // Add to 1m rolling window
                state.Add1mCandle(candle1m);

                // Check if this 1m close completes a 15m bar
                bool completed15m = CompletesBar(candle1m, Timeframe.M15, state);
                bool completed5m = CompletesBar(candle1m, Timeframe.M5, state);

                // B-03: Process 15m BEFORE 5m on shared boundaries
                if (completed15m)
                {
                    var agg15m = Aggregate(state, Timeframe.M15, candle1m);
                    if (agg15m != null)
                    {
                        state.Add15mCandle(agg15m);
                        TryUpdateRegime(symbol, state, parameters);
                    }
                }

                if (completed5m)
                {
                    var agg5m = Aggregate(state, Timeframe.M5, candle1m);
                    if (agg5m != null)
                    {
                        state.Add5mCandle(agg5m);
                        TryUpdateIndicators(symbol, state, parameters);
                        var (signal, decision) = TryGenerateSignalWithDecision(symbol, state, agg5m, parameters);
                        result.TotalEvaluations++;
                        if (signal != null)
                        {
                            result.Signals.Add(signal);
                        }
                        else if (decision != null)
                        {
                            result.NoTradeCount++;
                            foreach (var rc in decision.ReasonCodes)
                            {
                                var key = rc.ToString();
                                result.RejectReasonCounts[key] = result.RejectReasonCounts.GetValueOrDefault(key) + 1;
                            }
                        }
                    }
                }

                // Checkpoint
                if (processedCount % CheckpointEveryBars == 0 && replayRunId.HasValue)
                {
                    await _replayRepo.UpdateRunProgressAsync(replayRunId.Value,
                        processedCount, result.Signals.Count, result.Outcomes.Count,
                        candle1m.OpenTime, ct);
                }
            }

            // 4) Finalize outcomes for all generated signals
            FinalizeOutcomes(result, state, parameters);

            // 5) Persist artifacts if requested
            if (persistArtifacts)
            {
                foreach (var sig in result.Signals)
                    await _signalRepo.InsertSignalAsync(sig, ct);

                foreach (var outcome in result.Outcomes)
                {
                    await _signalRepo.InsertOutcomeAsync(outcome, ct);
                    await _signalRepo.UpdateSignalStatusAsync(outcome.SignalId,
                        outcome.OutcomeLabel == OutcomeLabel.EXPIRED ? SignalStatus.EXPIRED :
                        outcome.OutcomeLabel == OutcomeLabel.PENDING ? SignalStatus.OPEN :
                        SignalStatus.CLOSED, ct);
                }
            }

            result.CandlesProcessed = processedCount;

            if (replayRunId.HasValue)
                await _replayRepo.UpdateRunFinishedAsync(replayRunId.Value, RunStatus.Completed,
                    processedCount, result.Signals.Count, result.Outcomes.Count, ct);

            _logger.LogInformation("Replay completed: {Candles} candles, {Signals} signals, {Outcomes} outcomes",
                processedCount, result.Signals.Count, result.Outcomes.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Replay failed at candle {Count}", processedCount);
            if (replayRunId.HasValue)
                await _replayRepo.UpdateRunStatusAsync(replayRunId.Value, RunStatus.Failed, ex.Message, ct);
            throw;
        }

        return result;
    }

    /// <summary>Run replay in-memory only (no DB persistence). Used by optimizer.</summary>
    public async Task<ReplayResult> RunInMemoryAsync(
        string symbol, DateTimeOffset startUtc, DateTimeOffset endUtc,
        StrategyParameters parameters, CancellationToken ct = default)
        => await RunAsync(symbol, startUtc, endUtc, parameters, null, false, ct);

    private static bool CompletesBar(RichCandle candle1m, Timeframe tf, ReplayState state)
    {
        var bucket = tf.Floor(candle1m.OpenTime);
        var nextBucket = bucket.Add(tf.Duration);
        // The next 1m candle would be in the next bucket
        var next1m = candle1m.OpenTime.AddMinutes(1);
        return next1m >= nextBucket;
    }

    private RichCandle? Aggregate(ReplayState state, Timeframe tf, RichCandle trigger1m)
    {
        var bucket = tf.Floor(trigger1m.OpenTime);
        var nextBucket = bucket.Add(tf.Duration);
        var candles = state.Get1mCandlesInRange(bucket, nextBucket);

        if (candles.Count == 0) return null;

        var aggregated = CandleAggregator.Aggregate(candles, tf);
        if (aggregated.Count == 0) return null;

        var agg = aggregated[0];
        bool complete = candles.Count >= tf.Minutes;
        return agg with { IsClosed = complete };
    }

    private static void TryUpdateRegime(string symbol, ReplayState state, StrategyParameters p)
    {
        if (state.Candles15m.Count < p.WarmUpPeriod) return;

        var indicators = IndicatorEngine.ComputeAll(symbol, "15m", state.Candles15m, p);
        var valid = indicators.Where(s => !s.IsProvisional).ToList();
        if (valid.Count <= p.RegimeSlopeCandles) return;

        state.CurrentRegime = RegimeAnalyzer.Classify(symbol, valid, p);
    }

    private static void TryUpdateIndicators(string symbol, ReplayState state, StrategyParameters p)
    {
        if (state.Candles5m.Count < p.WarmUpPeriod) return;

        var indicators = IndicatorEngine.ComputeAll(symbol, "5m", state.Candles5m, p);
        var latest = indicators[^1];
        state.PrevSnap5m = state.LatestSnap5m;
        state.LatestSnap5m = latest;
        state.AddSnap5m(latest);
    }

    private (SignalRecommendation? Signal, SignalDecision? Decision) TryGenerateSignalWithDecision(
        string symbol, ReplayState state, RichCandle candle5m, StrategyParameters p)
    {
        if (state.LatestSnap5m == null || state.CurrentRegime == null) return (null, null);
        if (state.LatestSnap5m.IsProvisional) return (null, null);

        // Adaptive parameters: adjust for current market conditions (same as live)
        if (p.AdaptiveParametersEnabled)
        {
            var atrSma50 = MarketConditionClassifier.ComputeAtrSma50(state.RecentSnaps5m);
            var condition = MarketConditionClassifier.Classify(state.LatestSnap5m, state.CurrentRegime, candle5m, atrSma50, p);
            p = AdaptiveOverlayResolver.ApplyOverlays(p, condition, p.AdaptiveOverlayIntensity);
        }

        var (signal, decision) = SignalEngine.EvaluateWithDecision(symbol, state.CurrentRegime, state.LatestSnap5m,
            state.PrevSnap5m, candle5m, p, SourceMode.HISTORICAL_REPLAY);

        if (signal.Direction == SignalDirection.NO_TRADE) return (null, decision);

        var riskPolicy = p.ToRiskPolicy();
        decimal swingExtreme;
        if (state.Candles5m.Count >= 5)
        {
            var recent = state.Candles5m.Skip(state.Candles5m.Count - 5).ToList();
            swingExtreme = signal.Direction == SignalDirection.BUY
                ? recent.Min(c => c.MidLow)
                : recent.Max(c => c.MidHigh);
        }
        else
        {
            swingExtreme = signal.Direction == SignalDirection.BUY
                ? candle5m.MidLow : candle5m.MidHigh;
        }

        decimal spreadPct = state.LatestSnap5m.CloseMid > 0
            ? state.LatestSnap5m.Spread / state.LatestSnap5m.CloseMid : 0;

        var risk = RiskManager.ComputeRisk(signal.Direction, state.LatestSnap5m.CloseMid,
            state.LatestSnap5m.Atr14, swingExtreme, spreadPct, riskPolicy);

        if (!risk.Allowed) return (null, decision);

        return (signal with
        {
            EntryPrice = risk.EntryPrice,
            TpPrice = risk.TakeProfit,
            SlPrice = risk.StopLoss,
            RiskUsd = risk.RiskUsd,
            RiskPercent = risk.RiskPercent
        }, decision);
    }

    private static void FinalizeOutcomes(ReplayResult result, ReplayState state, StrategyParameters p)
    {
        foreach (var signal in result.Signals)
        {
            // Get future 5m candles after signal time
            var futureCandles = state.Candles5m
                .Where(c => c.OpenTime >= signal.SignalTimeUtc)
                .Take(p.OutcomeTimeoutBars)
                .ToList();

            if (futureCandles.Count == 0) continue;

            var adjustedSignal = futureCandles.Count > 0
                ? RiskManager.ReanchorToFilledEntry(signal, futureCandles[0].MidOpen)
                : signal;

            var future1m = state.Get1mCandlesInRange(
                signal.SignalTimeUtc,
                futureCandles[^1].OpenTime.Add(Timeframe.M5.Duration));

            var outcome = OutcomeEvaluator.Evaluate(adjustedSignal, futureCandles, p,
                Timeframe.M5, future1m, Timeframe.M1);
            result.Outcomes.Add(outcome);
        }
    }
}

/// <summary>Mutable state maintained during a replay run.</summary>
public sealed class ReplayState
{
    private readonly List<RichCandle> _candles1m = [];
    private readonly List<RichCandle> _candles5m = [];
    private readonly List<RichCandle> _candles15m = [];
    private readonly int _maxWindow;

    public ReplayState(StrategyParameters p)
    {
        // Keep enough candles for all lookback needs
        _maxWindow = Math.Max(p.WarmUpPeriod + 20, p.OutcomeTimeoutBars + 20);
    }

    public IReadOnlyList<RichCandle> Candles1m => _candles1m;
    public IReadOnlyList<RichCandle> Candles5m => _candles5m;
    public IReadOnlyList<RichCandle> Candles15m => _candles15m;

    public RegimeResult? CurrentRegime { get; set; }
    public IndicatorSnapshot? LatestSnap5m { get; set; }
    public IndicatorSnapshot? PrevSnap5m { get; set; }

    // Rolling indicator snapshots for adaptive parameter ATR SMA computation
    private readonly List<IndicatorSnapshot> _recentSnaps5m = [];
    public IReadOnlyList<IndicatorSnapshot> RecentSnaps5m => _recentSnaps5m;

    public void AddSnap5m(IndicatorSnapshot snap)
    {
        _recentSnaps5m.Insert(0, snap);
        if (_recentSnaps5m.Count > 60)
            _recentSnaps5m.RemoveAt(60);
    }

    public void Add1mCandle(RichCandle c)
    {
        _candles1m.Add(c);
        // Keep window bounded to prevent unbounded memory growth
        if (_candles1m.Count > _maxWindow * 15)
            _candles1m.RemoveRange(0, _candles1m.Count - _maxWindow * 15);
    }

    public void Add5mCandle(RichCandle c)
    {
        _candles5m.Add(c);
        if (_candles5m.Count > _maxWindow)
            _candles5m.RemoveRange(0, _candles5m.Count - _maxWindow);
    }

    public void Add15mCandle(RichCandle c)
    {
        _candles15m.Add(c);
        if (_candles15m.Count > _maxWindow)
            _candles15m.RemoveRange(0, _candles15m.Count - _maxWindow);
    }

    public IReadOnlyList<RichCandle> Get1mCandlesInRange(DateTimeOffset start, DateTimeOffset end)
        => _candles1m.Where(c => c.OpenTime >= start && c.OpenTime < end).ToList();
}

/// <summary>Results from a replay run.</summary>
public sealed class ReplayResult
{
    public int CandlesProcessed { get; set; }
    public int TotalEvaluations { get; set; }
    public int NoTradeCount { get; set; }
    public Dictionary<string, int> RejectReasonCounts { get; } = new();
    public List<SignalRecommendation> Signals { get; } = [];
    public List<SignalOutcome> Outcomes { get; } = [];

    public ReplayMetrics ComputeMetrics()
    {
        var completed = Outcomes.Where(o =>
            o.OutcomeLabel != OutcomeLabel.PENDING).ToList();
        var metricEligible = completed.Where(o => o.OutcomeLabel != OutcomeLabel.AMBIGUOUS).ToList();

        int wins = metricEligible.Count(o => o.OutcomeLabel == OutcomeLabel.WIN);
        int losses = metricEligible.Count(o => o.OutcomeLabel == OutcomeLabel.LOSS);
        int expired = metricEligible.Count(o => o.OutcomeLabel == OutcomeLabel.EXPIRED);
        int ambiguous = completed.Count(o => o.OutcomeLabel == OutcomeLabel.AMBIGUOUS);
        int pending = Outcomes.Count(o => o.OutcomeLabel == OutcomeLabel.PENDING);

        decimal totalPnl = metricEligible.Sum(o => o.PnlR);
        decimal sumPositive = metricEligible.Where(o => o.PnlR > 0).Sum(o => o.PnlR);
        decimal sumNegative = Math.Abs(metricEligible.Where(o => o.PnlR < 0).Sum(o => o.PnlR));
        decimal profitFactor = sumNegative > 0 ? sumPositive / sumNegative
            : sumPositive > 0 ? 999m : 0m;

        // Max drawdown in R
        decimal maxDD = 0, peak = 0, cumulative = 0;
        foreach (var o in metricEligible.OrderBy(o => o.SignalId))
        {
            cumulative += o.PnlR;
            if (cumulative > peak) peak = cumulative;
            var dd = peak - cumulative;
            if (dd > maxDD) maxDD = dd;
        }

        decimal winRate = (wins + losses) > 0 ? (decimal)wins / (wins + losses) : 0;
        decimal avgWinR = wins > 0 ? metricEligible.Where(o => o.PnlR > 0).Average(o => o.PnlR) : 0;
        decimal avgLossR = losses > 0 ? Math.Abs(metricEligible.Where(o => o.PnlR < 0).Average(o => o.PnlR)) : 0;
        decimal expectancy = winRate * avgWinR - (1 - winRate) * avgLossR;

        return new ReplayMetrics
        {
            TradeCount = metricEligible.Count,
            Wins = wins,
            Losses = losses,
            Expired = expired,
            Ambiguous = ambiguous,
            Pending = pending,
            WinRate = winRate,
            AvgPnlR = metricEligible.Count > 0 ? totalPnl / metricEligible.Count : 0,
            TotalPnlR = totalPnl,
            ProfitFactor = profitFactor,
            MaxDrawdownR = maxDD,
            ExpectancyR = expectancy,
            TimeoutRate = metricEligible.Count > 0 ? (decimal)expired / metricEligible.Count : 0,
            NoTradeRate = TotalEvaluations > 0 ? (decimal)NoTradeCount / TotalEvaluations : 0,
            SignalDensity = CandlesProcessed > 0
                ? (decimal)Signals.Count / (CandlesProcessed / 5m) // per 5m bar
                : 0
        };
    }
}
