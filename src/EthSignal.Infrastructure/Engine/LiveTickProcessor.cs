using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Apis;
using EthSignal.Infrastructure.Db;
using EthSignal.Infrastructure.Engine.ML;
using EthSignal.Infrastructure.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EthSignal.Infrastructure.Engine;

/// <summary>
/// TF-5: Optional warm-start evaluation after backfill.
/// TF-4: Regime recovery provenance + freshness check.
/// FR-1/TF-2: Persists decision audit for every evaluation.
/// LOG-1/LOG-2: Info-level decision logging.
/// </summary>

public sealed class LiveTickProcessor
{
    private const int VolumeRefreshEveryTicks = 15;
    private const int SentimentRefreshEveryTicks = 10;
    private const int IndicatorCacheRefreshEveryTicks = 5;
    private const int ProvisionalIndicatorPersistEveryTicks = 25;

    private readonly ITickProvider _tickProvider;
    private readonly ICapitalClient _api;
    private readonly ICandleRepository _repo;
    private readonly IIndicatorRepository _indicatorRepo;
    private readonly IRegimeRepository _regimeRepo;
    private readonly ISignalRepository _signalRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly ITickSnapshotRepository _tickSnapshotRepo;
    private readonly IDecisionAuditRepository _decisionAuditRepo;
    private readonly MarketStateCache _marketState;
    private readonly IParameterProvider _paramProvider;
    private readonly ILogger<LiveTickProcessor> _logger;
    private readonly MlInferenceService _mlInference;
    private readonly SignalFrequencyManager _frequencyManager;
    private readonly IMlFeatureRepository _mlFeatureRepo;
    private readonly IMlPredictionRepository _mlPredictionRepo;
    private readonly IBtcContextProvider _btcContextProvider;
    private readonly MlDriftDetector _mlDriftDetector;
    private readonly MarketAdaptiveParameterService _adaptiveService;
    private readonly IParameterRepository _paramRepo;
    private readonly ITelegramNotifier _telegram;
    private readonly bool _uiPriceOnly;
    private readonly decimal _syntheticVolumePerTick;
    private readonly Sentiment _defaultSentiment;
    private readonly int _tickSnapshotEvery;
    private readonly TimeSpan _partialHtfWarnGrace;

    // 1m candle is built from ticks; higher-TF running candles updated on each tick
    private RichCandle? _openCandle1m;
    private readonly Dictionary<Timeframe, RichCandle> _openCandlesHTF = new();
    private SpotPrice? _previousSpot; // P1-02: track previous tick for proper rollover
    private Sentiment _sentiment = new(0m, 0m);
    private RegimeResult? _currentRegimeResult;
    // Track latest and previous indicator snapshots per timeframe for multi-TF evaluation
    private readonly Dictionary<string, IndicatorSnapshot?> _latestSnaps = new();
    private readonly Dictionary<string, IndicatorSnapshot?> _prevSnaps = new();
    private SignalRecommendation? _lastSignal;
    private SignalDecision? _lastDecision; // TF-7: track latest decision for dashboard
    private bool _regimeStale = true; // REQ-NS-004: start stale until first successful regime classification
    private int _tickCount;
    private DateTimeOffset _runStartedUtc = DateTimeOffset.UtcNow;

    // ML feature extraction tracking
    private readonly MlRecentSnapshotBuffer _recentSnapBuffer = new();
    private int _barsSinceLastSignal;

    // Dedupe cache for provisional (running-candle) signals
    private readonly HashSet<string> _provisionalSignalKeys = new();
    private DateTimeOffset? _lastScalpBarTime; // cooldown tracker for 1m scalp signals
    private int _missedSignalDueToExceptionCount;
    private DateTimeOffset? _lastEvaluationExceptionUtc;

    private sealed record MlEvaluationArtifacts(Guid EvaluationId, MlPrediction? Prediction);

    public int MissedSignalDueToExceptionCount => Volatile.Read(ref _missedSignalDueToExceptionCount);
    public DateTimeOffset? LastEvaluationExceptionUtc => _lastEvaluationExceptionUtc;

    // Convenience accessors for backward compatibility
    private IndicatorSnapshot? _latestSnap5m
    {
        get => _latestSnaps.GetValueOrDefault("5m");
        set => _latestSnaps["5m"] = value;
    }
    private IndicatorSnapshot? _prevSnap5m
    {
        get => _prevSnaps.GetValueOrDefault("5m");
        set => _prevSnaps["5m"] = value;
    }

    public LiveTickProcessor(ITickProvider tickProvider, ICapitalClient api, ICandleRepository repo,
        IIndicatorRepository indicatorRepo, IRegimeRepository regimeRepo,
        ISignalRepository signalRepo, IAuditRepository auditRepo,
        ITickSnapshotRepository tickSnapshotRepo,
        IDecisionAuditRepository decisionAuditRepo,
        MarketStateCache marketState, IParameterProvider paramProvider,
        MlInferenceService mlInference, SignalFrequencyManager frequencyManager,
        IMlFeatureRepository mlFeatureRepo, IMlPredictionRepository mlPredictionRepo,
        IBtcContextProvider btcContextProvider,
        MlDriftDetector mlDriftDetector,
        MarketAdaptiveParameterService adaptiveService, IParameterRepository paramRepo,
        ITelegramNotifier telegram,
        IConfiguration config,
        ILogger<LiveTickProcessor> logger)
    {
        _tickProvider = tickProvider;
        _api = api;
        _repo = repo;
        _indicatorRepo = indicatorRepo;
        _regimeRepo = regimeRepo;
        _signalRepo = signalRepo;
        _auditRepo = auditRepo;
        _tickSnapshotRepo = tickSnapshotRepo;
        _decisionAuditRepo = decisionAuditRepo;
        _marketState = marketState;
        _paramProvider = paramProvider;
        _mlInference = mlInference;
        _frequencyManager = frequencyManager;
        _mlFeatureRepo = mlFeatureRepo;
        _mlPredictionRepo = mlPredictionRepo;
        _btcContextProvider = btcContextProvider;
        _mlDriftDetector = mlDriftDetector;
        _adaptiveService = adaptiveService;
        _paramRepo = paramRepo;
        _telegram = telegram;
        _logger = logger;
        _uiPriceOnly = ParseBool(config["HighFreqTicks:UiPriceOnly"], fallback: true);
        _syntheticVolumePerTick = Math.Max(0.1m,
            ParseDecimal(config["HighFreqTicks:SyntheticVolumePerTick"], fallback: 1m));

        var defaultBuyerPct = ParseDecimal(config["HighFreqTicks:DefaultBuyerPct"], fallback: 50m);
        defaultBuyerPct = Math.Clamp(defaultBuyerPct, 0m, 100m);
        _defaultSentiment = new Sentiment(defaultBuyerPct, 100m - defaultBuyerPct);
        _tickSnapshotEvery = Math.Max(1, (int)ParseDecimal(config["HighFreqTicks:TickSnapshotEvery"], fallback: 1m));
        var partialWarnGraceMinutes = Math.Max(0, (int)ParseDecimal(
            config["HighFreqTicks:PartialHtfWarnGraceMinutes"], fallback: 7m));
        _partialHtfWarnGrace = TimeSpan.FromMinutes(partialWarnGraceMinutes);
    }

    private static bool ParseBool(string? raw, bool fallback) =>
        bool.TryParse(raw, out var value) ? value : fallback;

    private static decimal ParseDecimal(string? raw, decimal fallback) =>
        decimal.TryParse(raw, out var value) ? value : fallback;

    public async Task RunAsync(string symbol, string epic, CancellationToken ct)
    {
        _runStartedUtc = DateTimeOffset.UtcNow;
        _logger.LogInformation("Live tick processor starting for {Symbol}", symbol);

        if (_uiPriceOnly)
        {
            _sentiment = _defaultSentiment;
            _logger.LogInformation(
                "UiPriceOnly mode: using Playwright-inspected prices for live quote/candle pipeline (no API sentiment/volume pulls)");
        }
        else
        {
            _sentiment = await _api.GetSentimentAsync(epic, ct);
            var initialSpot = await _api.GetSpotPriceAsync(epic, ct);
            _marketState.LatestSpot = initialSpot;
            var now = DateTimeOffset.UtcNow;

            _logger.LogInformation("Initial spot price: Bid={Bid} Ask={Ask} Mid={Mid}",
                initialSpot.Bid, initialSpot.Ask, initialSpot.Mid);

            // Only create 1m open candle from ticks
            _openCandle1m = MakeCandle(Timeframe.M1.Floor(now), initialSpot);
            _previousSpot = initialSpot;
        }

        // Preload ML snapshot buffer from DB so features are warm from the first
        // live candle close.  300 bars @ 5m = 25 h — enough for prior-day context,
        // 4h realized vol, and ATR percentile rank.
        await PreloadMlSnapshotBufferAsync(symbol, ct);

        // Load last known regime
        _currentRegimeResult = await _regimeRepo.GetLatestAsync(symbol, ct);
        if (_currentRegimeResult != null)
            _marketState.UpdateRegime(Timeframe.M15.Name, _currentRegimeResult);
        _logger.LogInformation("Loaded regime: {Regime}", _currentRegimeResult?.Regime.ToString() ?? "UNKNOWN");

        // FR-4 / LOG-3: Log regime recovery provenance
        {
            var recoveryTime = DateTimeOffset.UtcNow;
            int? ageBars = null;
            string freshness;
            var p = _paramProvider.GetActive();

            if (_currentRegimeResult == null)
            {
                freshness = "UNAVAILABLE";
            }
            else
            {
                var regimeAge = recoveryTime - _currentRegimeResult.CandleOpenTimeUtc;
                var biasTfMinutes = Math.Max(1, Timeframe.ByName(p.TimeframeBias).Minutes);
                ageBars = (int)(regimeAge.TotalMinutes / biasTfMinutes);
                freshness = ageBars > p.MaxRecoveredRegimeAgeBars ? "STALE" : "FRESH";
            }

            _logger.LogInformation(
                "RegimeRecovery symbol={Symbol} regime={Regime} regimeBarTime={RegimeBarTime} recoveryTime={RecoveryTime} ageBars={AgeBars} freshness={Freshness}",
                symbol,
                _currentRegimeResult?.Regime.ToString() ?? "NONE",
                _currentRegimeResult?.CandleOpenTimeUtc.ToString("o") ?? "N/A",
                recoveryTime.ToString("o"),
                ageBars,
                freshness);
        }

        await WarmPopulateRegimeCacheAsync(symbol, ct);

        await ExpireStaleOpenSignalsAsync(symbol, ct);

        await RecoverStartupCandleStateAsync(symbol, ct);

        PrintHeader(symbol);
        // Reserve lines for: regime + 3 timeframes + signal
        for (var i = 0; i < 5; i++)
            Console.WriteLine();

        // TF-5: Optional warm-start evaluation of the latest fully closed 5m bar
        {
            var p = _paramProvider.GetActive();
            if (p.WarmStartEvaluateLatestClosed5m && _currentRegimeResult != null)
            {
                try
                {
                    var closed5m = await _repo.GetClosedCandlesAsync(Timeframe.M5, symbol, p.WarmUpPeriod + 10, ct);
                    if (closed5m.Count >= p.WarmUpPeriod)
                    {
                        var latestBar = closed5m[^1];
                        var barOpenTime = latestBar.OpenTime;

                        // NFR-3: Check if decision already exists for this bar
                        var exists = await _decisionAuditRepo.ExistsForBarAsync(symbol, "5m", barOpenTime, SourceMode.STARTUP_WARM, ct);
                        if (!exists)
                        {
                            var snaps = IndicatorEngine.ComputeAll(symbol, Timeframe.M5.Name, closed5m.ToList(), p);
                            var nonProv = snaps.Where(s => !s.IsProvisional).ToList();
                            if (nonProv.Count >= 2)
                            {
                                var warmSnap = nonProv[^1];
                                var warmPrev = nonProv[^2];

                                // Issue #6: Apply adaptive parameter system to warm-up path
                                // so warm-up decisions produce the same adaptive fields as live.
                                var warmBaseParams = p;
                                MarketConditionClass? warmConditionClass = null;
                                string? warmAdaptedJson = null;
                                var warmRecentSnaps = nonProv.TakeLast(Math.Min(50, nonProv.Count)).ToList();
                                var (warmAdaptedParams, warmCondition) = _adaptiveService.AdaptParameters(
                                    p, warmSnap, _currentRegimeResult, latestBar, Timeframe.M5, warmRecentSnaps);
                                p = warmAdaptedParams;
                                warmConditionClass = warmCondition;
                                warmAdaptedJson = MarketAdaptiveParameterService.BuildOverlayDiffsJson(warmBaseParams, p);

                                // MTF confluence: use the highest directional regime at/above 5m
                                // so warm-start replay aligns with the same bias rule as live evaluation.
                                var warmRegime = GetBiasRegimeFor(Timeframe.M5);
                                var (_, warmDecision) = SignalEngine.EvaluateWithDecision(
                                    symbol, warmRegime, warmSnap, warmPrev, latestBar, p,
                                    SourceMode.STARTUP_WARM, p.StrategyVersion, evaluationTf: Timeframe.M5);

                                // Issue #1/#2/#6: Stamp adaptive fields onto warm-up decision
                                warmDecision = warmDecision with
                                {
                                    MarketConditionClass = warmConditionClass?.ToKey(),
                                    AdaptedParametersJson = warmAdaptedJson
                                };

                                await _decisionAuditRepo.InsertDecisionAsync(warmDecision, ct);

                                _logger.LogInformation(
                                    "WarmStart SignalDecision symbol={Symbol} barTime={BarTime} decision={Decision} outcome={Outcome} reasonCodes=[{ReasonCodes}] regime={Regime} sourceMode=STARTUP_WARM",
                                    warmDecision.Symbol,
                                    warmDecision.BarTimeUtc.ToString("o"),
                                    warmDecision.DecisionType,
                                    warmDecision.OutcomeCategory,
                                    string.Join(",", warmDecision.ReasonCodes),
                                    warmDecision.UsedRegime?.ToString() ?? "NONE");
                            }
                        }
                        else
                        {
                            _logger.LogInformation("WarmStart skipped — decision already exists for bar {BarTime}", barOpenTime.ToString("o"));
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "WarmStart evaluation failed (non-fatal)");
                }
            }
        }

        await _tickProvider.StartAsync(epic, ct);
        _logger.LogInformation("[TickProvider] Kind={Kind}, starting tick stream",
            _tickProvider.Kind);

        await foreach (var spot in _tickProvider.ReadAllAsync(ct))
        {
            var tickStart = DateTimeOffset.UtcNow;
            _tickCount++;

            try
            {
                _marketState.LatestSpot = spot;
                var tickTime = DateTimeOffset.UtcNow;

                if (_tickCount % _tickSnapshotEvery == 0)
                {
                    try
                    {
                        await _tickSnapshotRepo.InsertAsync(symbol, epic, spot, _tickProvider.Kind.ToString(), ct);
                    }
                    catch (Exception tickDbEx)
                    {
                        _logger.LogWarning(tickDbEx, "Failed to persist UI tick snapshot");
                    }
                }

                if (_openCandle1m == null)
                {
                    _openCandle1m = MakeCandle(Timeframe.M1.Floor(tickTime), spot) with
                    {
                        Volume = _uiPriceOnly ? _syntheticVolumePerTick : 0m
                    };
                    _previousSpot = spot;
                }

                if (!_uiPriceOnly && _tickCount % SentimentRefreshEveryTicks == 0)
                    _sentiment = await _api.GetSentimentAsync(epic, ct);

                decimal latestApiVolume = 0m;
                if (!_uiPriceOnly && _tickCount % VolumeRefreshEveryTicks == 0)
                {
                    try
                    {
                        var vNow = DateTimeOffset.UtcNow;
                        var apiCandles = await _api.GetCandlesAsync(
                            epic, "MINUTE", vNow.AddMinutes(-2), vNow, 2, ct);
                        var current1mOpen = Timeframe.M1.Floor(tickTime);
                        var match = apiCandles.FirstOrDefault(c => c.OpenTime == current1mOpen);
                        if (match != null) latestApiVolume = match.Volume;
                    }
                    catch (Exception volEx)
                    {
                        if (volEx.Message.Contains("error.prices.not-found",
                            StringComparison.OrdinalIgnoreCase))
                            _logger.LogDebug("Volume: no data (market closed?) — Vol=0");
                        else
                            _logger.LogWarning(volEx, "Volume refresh failed — Vol=0");
                    }
                }

                // ─── 1m candle management ───────────────────────
                var expected1m = Timeframe.M1.Floor(tickTime);
                var closed1m = false;
                RichCandle? finalizedCandle1m = null;

                if (_openCandle1m != null && expected1m > _openCandle1m.OpenTime)
                {
                    // P1-02 FIX: Close old candle with PREVIOUS tick (last price before boundary)
                    finalizedCandle1m = _previousSpot != null
                        ? UpdateCandle(_openCandle1m, _previousSpot) with { IsClosed = true }
                        : _openCandle1m with { IsClosed = true };

                    // U-05 FIX: Repair OHLC invariants before persisting
                    if (!finalizedCandle1m.IsOhlcValid())
                    {
                        _logger.LogWarning("1m candle {Time} has invalid OHLC — repairing",
                            finalizedCandle1m.OpenTime.ToString("yyyy-MM-dd HH:mm"));
                        finalizedCandle1m = finalizedCandle1m.RepairOhlc();
                    }

                    // T3-10: Normalize synthetic volume in UI-only mode.
                    // Tick-accumulated volume is proportional to Playwright poll rate (e.g., 5Hz × 60s = 300 units),
                    // making VolumeSma20 comparisons noisy and session-dependent.
                    // Replace with a fixed 1.0 unit per 1m candle so every closed bar has identical volume,
                    // making VolumeSma20 and VolumeMultiplierMin comparisons stable and meaningful.
                    if (_uiPriceOnly)
                    {
                        finalizedCandle1m = finalizedCandle1m with { Volume = _syntheticVolumePerTick };
                    }

                    // C-02: Flag zero-volume candles so they can be excluded from SMA calculations
                    if (finalizedCandle1m.Volume == 0)
                    {
                        _logger.LogWarning("1m candle {Time} has Vol=0 — flagging as suspect",
                            finalizedCandle1m.OpenTime.ToString("yyyy-MM-dd HH:mm"));
                        finalizedCandle1m = finalizedCandle1m with { BuyerPct = -1m, SellerPct = -1m };
                    }

                    // Persist the closed 1m candle
                    await _repo.CloseCandlesAsync(symbol, [(Timeframe.M1, finalizedCandle1m)], ct);
                    _logger.LogInformation("1m closed: {Time} MidC={Close} Vol={Vol}",
                        finalizedCandle1m.OpenTime.ToString("yyyy-MM-dd HH:mm"),
                        finalizedCandle1m.MidClose, finalizedCandle1m.Volume);
                    _marketState.RecordCandleClose("1m", finalizedCandle1m.OpenTime);
                    closed1m = true;

                    // Open new 1m candle with CURRENT tick
                    _openCandle1m = MakeCandle(expected1m, spot) with
                    {
                        Volume = _uiPriceOnly ? _syntheticVolumePerTick : 0m
                    };
                }
                else if (_openCandle1m != null)
                {
                    // Update within same 1m bucket
                    var vol = latestApiVolume > 0
                        ? latestApiVolume
                        : (_uiPriceOnly ? _openCandle1m.Volume + _syntheticVolumePerTick : _openCandle1m.Volume);
                    _openCandle1m = UpdateCandle(_openCandle1m, spot, vol);
                }

                // ─── Update running higher-TF candles with current tick ─────
                foreach (var htf in new[] { Timeframe.M5, Timeframe.M15, Timeframe.M30, Timeframe.H1, Timeframe.H4 })
                {
                    var bucket = htf.Floor(tickTime);
                    if (_openCandlesHTF.TryGetValue(htf, out var existing) && existing.OpenTime == bucket)
                    {
                        var htfVol = _uiPriceOnly
                            ? existing.Volume + _syntheticVolumePerTick
                            : existing.Volume;
                        _openCandlesHTF[htf] = UpdateCandle(existing, spot, htfVol);
                    }
                    else
                    {
                        _openCandlesHTF[htf] = MakeCandle(bucket, spot) with
                        {
                            Volume = _uiPriceOnly ? _syntheticVolumePerTick : 0m
                        };
                    }
                }

                // Persist open candles (1m + all HTFs), throttled to every 10 ticks (~2s at 5Hz)
                if (_openCandle1m != null)
                {
                    var openCandles = new Dictionary<Timeframe, RichCandle> { [Timeframe.M1] = _openCandle1m };
                    if (_tickCount % 10 == 0)
                    {
                        foreach (var (htf, candle) in _openCandlesHTF)
                            openCandles[htf] = candle;
                    }
                    await _repo.UpsertOpenCandlesAsync(symbol, openCandles, ct);
                }

                if (_tickCount % IndicatorCacheRefreshEveryTicks == 0)
                {
                    var pForIndicators = GetRuntimeParameters();
                    await RefreshIndicatorCacheAsync(symbol, pForIndicators, ct);
                }

                // ─── Derive higher TFs from closed 1m on boundary crossing ─────
                // Process from highest TF down so regime updates before signal generation
                if (closed1m && finalizedCandle1m != null)
                {
                    var pForM1Regime = GetRuntimeParameters();
                    await RefreshM1RegimeAsync(symbol, pForM1Regime, ct);

                    foreach (var tf in new[] { Timeframe.H4, Timeframe.H1, Timeframe.M30, Timeframe.M15, Timeframe.M5 })
                    {
                        var closedBucket = tf.Floor(finalizedCandle1m.OpenTime);
                        var nextBucket = closedBucket.Add(tf.Duration);

                        // Did this 1m close complete a higher-TF bucket?
                        // C-05: Use finalized candle time, not expected1m (which is tick-floor based)
                        if (finalizedCandle1m.OpenTime.Add(Timeframe.M1.Duration) >= nextBucket)
                        {
                            // Aggregate closed 1m candles in the completed bucket
                            var m1Candles = await _repo.GetClosedCandlesInRangeAsync(
                                Timeframe.M1, symbol, closedBucket, nextBucket, ct);

                            if (m1Candles.Count > 0)
                            {
                                var aggregated = CandleAggregator.Aggregate(m1Candles, tf);
                                if (aggregated.Count == 1)
                                {
                                    // Only store if we have enough 1m candles for completeness
                                    var expectedCount = tf.Minutes;
                                    var agg = aggregated[0] with { IsClosed = m1Candles.Count >= expectedCount };

                                    await _repo.BulkUpsertAsync(tf, symbol, [agg], ct);
                                    _logger.LogInformation("{Tf} candle {Status}: {Time} MidC={Close} Vol={Vol} ({M1Count}/{Expected} 1m bars)",
                                        tf.Name, agg.IsClosed ? "closed" : "partial",
                                        agg.OpenTime.ToString("yyyy-MM-dd HH:mm"),
                                        agg.MidClose, agg.Volume, m1Candles.Count, expectedCount);

                                    if (agg.IsClosed)
                                    {
                                        _openCandlesHTF.Remove(tf); // clear running candle; fresh one starts on next tick
                                        _marketState.RecordCandleClose(tf.Name, agg.OpenTime);
                                        await OnHigherTimeframeClosed(symbol, tf, agg, ct);
                                    }
                                    else
                                    {
                                        // During startup/restart, partial higher-TF buckets are expected.
                                        // Keep visibility but reduce noise until grace window elapses.
                                        if ((DateTimeOffset.UtcNow - _runStartedUtc) < _partialHtfWarnGrace)
                                        {
                                            _logger.LogInformation("{Tf} candle {Time} incomplete during startup grace: {M1Count}/{Expected} 1m bars — skipping indicator/regime/signal pipeline",
                                                tf.Name, agg.OpenTime.ToString("yyyy-MM-dd HH:mm"), m1Candles.Count, expectedCount);
                                        }
                                        else
                                        {
                                            _logger.LogWarning("{Tf} candle {Time} NOT closed: only {M1Count}/{Expected} 1m bars — skipping indicator/regime/signal pipeline",
                                                tf.Name, agg.OpenTime.ToString("yyyy-MM-dd HH:mm"), m1Candles.Count, expectedCount);
                                        }
                                    }
                                }
                                else
                                {
                                    // U-06: Unexpected aggregation result
                                    _logger.LogWarning("{Tf} boundary {Bucket}: aggregation produced {Count} candles (expected 1) from {M1Count} 1m bars",
                                        tf.Name, closedBucket.ToString("yyyy-MM-dd HH:mm"), aggregated.Count, m1Candles.Count);
                                }
                            }
                            else
                            {
                                // U-06: No 1m candles in range
                                _logger.LogWarning("{Tf} boundary {Bucket}: no closed 1m candles found in [{Start}, {End})",
                                    tf.Name, closedBucket.ToString("yyyy-MM-dd HH:mm"),
                                    closedBucket.ToString("HH:mm"), nextBucket.ToString("HH:mm"));
                            }
                        }
                    }
                }

                // ─── 1m scalping signal evaluation ────
                // Evaluate scalp signals on every closed 1m candle when enabled.
                if (closed1m && finalizedCandle1m != null)
                {
                    var scalpParams = _paramProvider.GetActive();
                    if (scalpParams.ScalpingEnabled)
                    {
                        var current5mBucket = Timeframe.M5.Floor(finalizedCandle1m.OpenTime);
                        await TryEvaluateOn1mClose(symbol, finalizedCandle1m, current5mBucket, ct);
                    }
                }

                _previousSpot = spot;
                PrintCandles(symbol);

                _marketState.RecordTickMetrics(_tickProvider.TickRateHz, _tickProvider.Kind);
                if (_tickCount % 60 == 0)
                {
                    _logger.LogDebug("Tick #{Tick} ({Hz:F1} Hz) Spot: {Mid}",
                        _tickCount, _tickProvider.TickRateHz, spot.Mid);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // HttpClient timeout or transient cancel (e.g. macOS sleep) — NOT a real shutdown
                _logger.LogWarning("Tick #{Tick} transient cancel (network timeout / sleep?) — retrying", _tickCount);
                _marketState.RecordError("Transient cancel — retrying");
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Tick error at tick #{Tick}", _tickCount);
                _marketState.RecordError(ex.Message);
                Console.Write("\x1b[5A");
                Console.Write($"\x1b[2K  Error: {ex.Message[..Math.Min(ex.Message.Length, 80)]}\n");
                for (var i = 0; i < 4; i++)
                    Console.Write("\x1b[2K\n");
            }

        }

        _logger.LogInformation("Live tick processor stopped");
    }

    private async Task PreloadMlSnapshotBufferAsync(string symbol, CancellationToken ct)
    {
        // Warm the ML snapshot buffer from the DB so that features that require
        // lookback history (prior-day H/L, 4h realized vol, ATR percentile) are
        // meaningful from the very first live candle close, not just after hours of
        // runtime accumulation.
        //
        // We preload 300 bars for each timeframe the ML pipeline evaluates on.
        // At 5m that covers 25 h, giving the session-structure and volatility
        // features full prior-day visibility.
        var timeframes = new[] { Timeframe.M1, Timeframe.M5, Timeframe.M15, Timeframe.M30, Timeframe.H1 };
        const int BarsToLoad = 300;

        foreach (var tf in timeframes)
        {
            try
            {
                var from = DateTimeOffset.UtcNow.AddMinutes(-(tf.Minutes * BarsToLoad));
                var to = DateTimeOffset.UtcNow.AddMinutes(tf.Minutes); // inclusive of current open bar
                var snaps = await _indicatorRepo.GetSnapshotsAsync(symbol, tf.Name, from, to, ct);
                if (snaps.Count == 0) continue;
                _recentSnapBuffer.Preload(tf.Name, snaps);
                _logger.LogInformation(
                    "[ML] Snapshot buffer preloaded: timeframe={Tf} bars={Count} from={From:yyyy-MM-dd HH:mm}",
                    tf.Name, snaps.Count, from);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ML] Failed to preload snapshot buffer for timeframe={Tf}", tf.Name);
            }
        }
    }

    private async Task RecoverStartupCandleStateAsync(string symbol, CancellationToken ct)
    {
        try
        {
            // Heal abrupt-shutdown residue: any open candle whose full duration ended
            // before the current 1m boundary should be marked closed.
            var boundary = Timeframe.M1.Floor(DateTimeOffset.UtcNow);
            await _repo.CloseOpenCandlesBeforeAsync(symbol, boundary, ct);

            // In UiPriceOnly mode we don't seed from REST spot. Resume the current
            // in-progress 1m candle if it already exists in DB to reduce continuity gaps.
            if (_openCandle1m == null)
            {
                var open1m = await _repo.GetOpenCandleAsync(Timeframe.M1, symbol, ct);
                if (open1m != null && open1m.OpenTime == boundary)
                {
                    _openCandle1m = open1m;
                    _previousSpot = new SpotPrice(open1m.BidClose, open1m.AskClose, open1m.MidClose, DateTimeOffset.UtcNow);
                    _logger.LogInformation(
                        "Startup gap-heal: resumed open 1m candle at {Time}",
                        open1m.OpenTime.ToString("yyyy-MM-dd HH:mm"));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup gap-heal skipped due to non-fatal error");
        }
    }

    private async Task RefreshM1RegimeAsync(string symbol, StrategyParameters p, CancellationToken ct)
    {
        try
        {
            var closed1m = await _repo.GetClosedCandlesAsync(Timeframe.M1, symbol, p.WarmUpPeriod + 10, ct);
            if (closed1m.Count < p.WarmUpPeriod)
                return;

            var snapshots = IndicatorEngine.ComputeAll(symbol, Timeframe.M1.Name, closed1m, p);
            var finalized = snapshots.Where(s => !s.IsProvisional).ToList();
            if (finalized.Count <= 4)
                return;

            var regime1m = RegimeAnalyzer.Classify(symbol, finalized, p);
            _marketState.UpdateRegime(Timeframe.M1.Name, regime1m);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[1m] Regime refresh skipped due to non-fatal error");
        }
    }

    /// <summary>
    /// MTF confluence: pick the regime that should bias the entry decision for the given TF.
    /// Walks UP the timeframe ladder starting at <paramref name="tf"/> and returns the first
    /// directional (BULLISH/BEARISH) regime it finds. If every higher TF is NEUTRAL, falls back
    /// to the per-TF regime, then to the global 15m regime.
    ///
    /// Why: when the lower TFs are choppy (NEUTRAL) but a higher TF has a clear trend,
    /// the higher-TF bias is the correct anchor for entries on the lower TF — this is the
    /// standard "trade in the direction of the higher-TF trend" rule. Without this, the
    /// regime alignment hard gate in SignalEngine blocks every entry whenever per-TF regime
    /// is NEUTRAL even though a clear directional bias exists one TF up.
    /// </summary>
    private RegimeResult GetBiasRegimeFor(Timeframe tf)
    {
        var ladder = new[] { Timeframe.M1, Timeframe.M5, Timeframe.M15, Timeframe.M30, Timeframe.H1, Timeframe.H4 };
        int startIdx = Array.IndexOf(ladder, tf);
        if (startIdx < 0) startIdx = 0;

        // Walk UP the ladder looking for a directional regime
        for (int i = startIdx; i < ladder.Length; i++)
        {
            var r = _marketState.GetCachedRegime(ladder[i].Name);
            if (r != null && r.Regime != Regime.NEUTRAL)
                return r;
        }

        // No directional regime found in or above this TF — use per-TF regime, then global
        return _marketState.GetCachedRegime(tf.Name) ?? _currentRegimeResult!;
    }

    /// <summary>
    /// Expire any OPEN signals left over from prior strategy versions or older than the
    /// outcome timeout window. Without this, stale OPEN signals from historical replays
    /// or old runs occupy the MaxOpenPositions slot and silently block every new live
    /// signal at the dedup gate. Self-healing on every restart.
    /// </summary>
    private async Task ExpireStaleOpenSignalsAsync(string symbol, CancellationToken ct)
    {
        try
        {
            var p = GetRuntimeParameters();
            var open = await _signalRepo.GetOpenSignalsAsync(symbol, ct);
            if (open.Count == 0) return;

            int expired = 0;
            foreach (var s in open)
            {
                bool wrongVersion = !string.Equals(s.StrategyVersion, p.StrategyVersion, StringComparison.Ordinal);

                // Expire if past the outcome timeout window for that TF
                var tfMinutes = Math.Max(1, Timeframe.ByName(s.Timeframe).Minutes);
                var ageMinutes = (DateTimeOffset.UtcNow - s.SignalTimeUtc).TotalMinutes;
                var timeoutMinutes = tfMinutes * p.OutcomeTimeoutBars;
                bool tooOld = ageMinutes > timeoutMinutes;

                if (wrongVersion || tooOld)
                {
                    await _signalRepo.UpdateSignalStatusAsync(s.SignalId, SignalStatus.EXPIRED, ct);
                    expired++;
                }
            }

            if (expired > 0)
            {
                _logger.LogInformation(
                    "Startup cleanup: expired {Count}/{Total} stale OPEN signals (wrong strategy version or past outcome timeout)",
                    expired, open.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stale signal cleanup failed (non-fatal)");
        }
    }

    private async Task WarmPopulateRegimeCacheAsync(string symbol, CancellationToken ct)
    {
        try
        {
            var p = GetRuntimeParameters();
            foreach (var tf in new[] { Timeframe.M1, Timeframe.M5, Timeframe.M15, Timeframe.M30, Timeframe.H1, Timeframe.H4 })
            {
                // FIX: Always recompute from freshly-synced candles. The previous shortcut
                // (reusing _currentRegimeResult for 15m, or skipping when cache had any value)
                // caused stale regimes after offline gap recovery — the persisted regime could
                // be hours old, blocking all signal evaluation via the freshness gate.
                var closed = await _repo.GetClosedCandlesAsync(tf, symbol, p.WarmUpPeriod + 10, ct);
                if (closed.Count < p.WarmUpPeriod)
                    continue;

                var snaps = IndicatorEngine.ComputeAll(symbol, tf.Name, closed, p);
                var finalized = snaps.Where(s => !s.IsProvisional).ToList();
                if (finalized.Count <= 4)
                    continue;

                var regime = RegimeAnalyzer.Classify(symbol, finalized, p);
                _marketState.UpdateRegime(tf.Name, regime);

                // Refresh the global 15m regime so the freshness gate uses the latest classification,
                // and persist it so subsequent restarts pick up the fresh regime.
                if (tf == Timeframe.M15)
                {
                    _currentRegimeResult = regime;
                    try
                    {
                        await _regimeRepo.UpsertAsync(regime, ct);
                    }
                    catch (Exception persistEx)
                    {
                        _logger.LogDebug(persistEx, "Persisting refreshed 15m regime failed (non-fatal)");
                    }
                    _logger.LogInformation(
                        "Warm regime refresh: 15m {Regime} (score={Score}) bar={BarTime}",
                        regime.Regime, regime.RegimeScore, regime.CandleOpenTimeUtc.ToString("o"));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Regime warm population skipped due to non-fatal error");
        }
    }

    /// <summary>
    /// Called when a 5m or 15m candle closes (derived from aggregated 1m).
    /// Handles indicator computation, regime classification, and signal generation.
    /// </summary>
    private async Task OnHigherTimeframeClosed(string symbol, Timeframe tf, RichCandle closedCandle, CancellationToken ct)
    {
        try
        {
            var p = GetRuntimeParameters();

            // B-05: Gate indicators/signals when recent unresolved gaps exist
            if (await _auditRepo.HasRecentUnresolvedGapsAsync(symbol, lookbackMinutes: tf.Minutes * p.GapBlockLookbackBars, ct))
            {
                _regimeStale = true; // REQ-NS-004: mark regime as stale during blackout
                _logger.LogWarning("Skipping {Tf} processing — unresolved gaps detected in recent data", tf.Name);
                return;
            }

            // REQ-NS-004: Recover regime after gap blackout clears
            if (_regimeStale && tf == Timeframe.M5)
            {
                _logger.LogInformation("Gap blackout cleared — forcing regime recompute from stored 15m data");
                var closed15m = await _repo.GetClosedCandlesAsync(Timeframe.M15, symbol, p.WarmUpPeriod + 10, ct);
                if (closed15m.Count >= p.WarmUpPeriod)
                {
                    var ind15m = IndicatorEngine.ComputeAll(symbol, Timeframe.M15.Name, closed15m, p);
                    var final15m = ind15m.Where(s => !s.IsProvisional).ToList();
                    if (final15m.Count > 4)
                    {
                        var regime = RegimeAnalyzer.Classify(symbol, final15m, p);
                        await _regimeRepo.UpsertAsync(regime, ct);
                        _currentRegimeResult = regime;
                        _marketState.UpdateRegime(Timeframe.M15.Name, regime);
                        _logger.LogInformation("Regime recovered to {Regime} (score={Score})", regime.Regime, regime.RegimeScore);
                    }
                }
                _regimeStale = false;
            }

            // Clean up provisional signal keys for this closed bucket
            _provisionalSignalKeys.RemoveWhere(k => k.StartsWith($"{tf.Name}|{closedCandle.OpenTime:O}|"));

            var closed = await _repo.GetClosedCandlesAsync(tf, symbol, p.WarmUpPeriod + 10, ct);
            if (closed.Count < p.WarmUpPeriod)
            {
                // U-06: Log warmup skip
                _logger.LogWarning("{Tf} pipeline skipped: only {Count}/{Required} closed candles (warmup not met)",
                    tf.Name, closed.Count, p.WarmUpPeriod);
                return;
            }

            var snapshot = IndicatorEngine.ComputeLatest(symbol, tf.Name, closed, p);
            if (snapshot != null)
            {
                // Only persist finalized (non-provisional) snapshots. On closed candles with
                // >= WarmUpPeriod bars the snapshot should never be provisional, but guard
                // explicitly to prevent DB corruption from unexpected edge cases.
                if (!snapshot.IsProvisional)
                    await _indicatorRepo.BulkUpsertAsync([snapshot], ct);
                _marketState.UpdateIndicator(tf.Name, snapshot);
                _logger.LogInformation("Indicator computed: {Tf} RSI={Rsi} EMA20={Ema20} ATR={Atr}",
                    tf.Name, snapshot.Rsi14.ToString("F1"), snapshot.Ema20.ToString("F2"), snapshot.Atr14.ToString("F2"));
            }

            // Track snapshots per timeframe for signal generation
            if (Timeframe.Signal.Contains(tf))
            {
                _prevSnaps[tf.Name] = _latestSnaps.GetValueOrDefault(tf.Name);
                _latestSnaps[tf.Name] = snapshot;

                // Track snapshots for ML feature extraction
                if (snapshot != null)
                    TrackSnapshotForMl(tf.Name, snapshot);

                // Generate signal on every signal-TF close
                if (snapshot != null && _currentRegimeResult != null)
                    await TryGenerateSignal(symbol, closedCandle, closed, tf, ct);
            }

            // Classify and cache regime on every signal timeframe close
            if (Timeframe.Signal.Contains(tf) && tf != Timeframe.M1)
            {
                var ind = IndicatorEngine.ComputeAll(symbol, tf.Name, closed, p);
                var finalized = ind.Where(s => !s.IsProvisional).ToList();
                if (finalized.Count > 4)
                {
                    var regime = RegimeAnalyzer.Classify(symbol, finalized, p);
                    _marketState.UpdateRegime(tf.Name, regime);

                    // Keep primary signal regime sourced from 15m for strategy consistency
                    if (tf == Timeframe.M15)
                    {
                        await _regimeRepo.UpsertAsync(regime, ct);
                        _currentRegimeResult = regime;
                        _regimeStale = false; // REQ-NS-004
                        _logger.LogInformation("Regime updated: {Regime} (score={Score})",
                            regime.Regime, regime.RegimeScore);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Indicator/regime computation failed for {Tf}", tf.Name);
        }
    }

    private async Task TryGenerateSignal(string symbol, RichCandle closedCandle,
        IReadOnlyList<RichCandle> closedHistory, Timeframe tf, CancellationToken ct)
    {
        MlEvaluationArtifacts? mlArtifacts = null;
        try
        {
            _logger.LogInformation("[LOG-TRACE] TryGenerateSignal: symbol={Symbol} tf={Tf} barTime={BarTime} close={Close} vol={Vol}", symbol, tf.Name, closedCandle.OpenTime.ToString("o"), closedCandle.MidClose, closedCandle.Volume);
            var p = GetRuntimeParameters();
            var riskPolicy = p.ToRiskPolicy();

            var latestSnap = _latestSnaps.GetValueOrDefault(tf.Name);
            var snap = latestSnap;
            if (snap == null)
                return;

            var prevSnap = _prevSnaps.GetValueOrDefault(tf.Name);

            // TF-4: Check regime freshness before evaluation
            if (_currentRegimeResult != null)
            {
                var regimeAge = closedCandle.OpenTime - _currentRegimeResult.CandleOpenTimeUtc;
                var biasTfMinutes = Math.Max(1, Timeframe.ByName(p.TimeframeBias).Minutes);
                var ageBars = (int)(regimeAge.TotalMinutes / biasTfMinutes);
                if (ageBars > p.MaxRecoveredRegimeAgeBars)
                {
                    var staleDecision = new SignalDecision
                    {
                        Symbol = symbol,
                        Timeframe = tf.Name,
                        DecisionTimeUtc = DateTimeOffset.UtcNow,
                        BarTimeUtc = closedCandle.OpenTime,
                        DecisionType = SignalDirection.NO_TRADE,
                        OutcomeCategory = OutcomeCategory.CONTEXT_NOT_READY,
                        UsedRegime = _currentRegimeResult.Regime,
                        UsedRegimeTimestamp = _currentRegimeResult.CandleOpenTimeUtc,
                        ReasonCodes = [RejectReasonCode.STALE_HTF_CONTEXT],
                        ReasonDetails = [$"[{tf.Name}] Regime age {ageBars} bars exceeds max {p.MaxRecoveredRegimeAgeBars}"],
                        IndicatorSnapshot = new Dictionary<string, decimal>(),
                        ParameterSetId = p.StrategyVersion,
                        SourceMode = SourceMode.LIVE
                    };
                    await _decisionAuditRepo.InsertDecisionAsync(staleDecision, ct);
                    _lastDecision = staleDecision;

                    _logger.LogInformation(
                        "[{Tf}] SignalDecision symbol={Symbol} barTime={BarTime} regime={Regime} decision=NO_TRADE outcome=CONTEXT_NOT_READY reasonCodes=STALE_HTF_CONTEXT",
                        tf.Name, symbol, closedCandle.OpenTime.ToString("o"),
                        _currentRegimeResult.Regime);
                    return;
                }
            }

            // MTF confluence: anchor entries on the highest directional regime found at or
            // above this TF. If 5m is NEUTRAL but 1h is BULLISH, the 1h trend bias drives
            // the entry alignment so the regime hard gate doesn't block valid trades.
            var tfRegime = GetBiasRegimeFor(tf);

            // Adaptive parameters: adjust for current market conditions
            // Issue #1/#2: capture base params before adaptation so we can compute overlay diffs
            var baseParams = p;
            MarketConditionClass? currentConditionClass = null;
            string? adaptedParametersJson = null;
            if (latestSnap != null)
            {
                var recentSnaps = _recentSnapBuffer.Get(tf.Name);
                var (adaptedParams, conditionClass) = _adaptiveService.AdaptParameters(
                    p, snap, _currentRegimeResult, closedCandle, tf, recentSnaps);
                p = adaptedParams;
                riskPolicy = p.ToRiskPolicy();
                currentConditionClass = conditionClass;
                adaptedParametersJson = MarketAdaptiveParameterService.BuildOverlayDiffsJson(baseParams, p);
            }

            // TF-1: Use EvaluateWithMl for ML-enhanced signal generation
            // First run a quick rule-based pass to get direction/score for feature extraction
            var (preSignal, preDecision) = SignalEngine.EvaluateWithDecision(
                symbol, tfRegime, snap, prevSnap, closedCandle, p,
                SourceMode.LIVE, p.StrategyVersion, evaluationTf: tf);
            mlArtifacts = await RunMlInferenceAsync(
                symbol, snap, prevSnap, closedCandle, tfRegime,
                preSignal.Direction, preSignal.ConfidenceScore, p, tf, ct);
            var mlPrediction = mlArtifacts?.Prediction;

            var (signal, decision) = SignalEngine.EvaluateWithMl(
                symbol, tfRegime, snap, prevSnap, closedCandle, p,
                mlPrediction, _frequencyManager,
                SourceMode.LIVE, p.StrategyVersion, evaluationTf: tf,
                preComputed: (preSignal, preDecision));

            // Issue #1/#2: Stamp adaptive fields onto the decision for audit persistence
            // FR-16: Thread the ML evaluation ID for end-to-end correlation
            // FR-8: Snapshot effective runtime parameters
            decision = decision with
            {
                MarketConditionClass = currentConditionClass?.ToKey(),
                AdaptedParametersJson = adaptedParametersJson,
                Origin = DecisionOrigin.CLOSED_BAR,
                EvaluationId = mlArtifacts?.EvaluationId ?? decision.EvaluationId,
                EffectiveRuntimeParametersJson = p.ToJson()
            };

            // TF-2 / FR-1: Persist decision audit for ALL outcomes (including NO_TRADE)
            await _decisionAuditRepo.InsertDecisionAsync(decision, ct);
            _lastDecision = decision;

            // LOG-1 / LOG-2: Info-level final decision log
            _logger.LogInformation(
                "[{Tf}] SignalDecision symbol={Symbol} barTime={BarTime} regime={Regime} decision={Decision} outcome={Outcome} score={Score} sourceMode=LIVE",
                tf.Name, decision.Symbol, decision.BarTimeUtc.ToString("o"),
                decision.UsedRegime?.ToString() ?? "NONE",
                decision.DecisionType, decision.OutcomeCategory,
                decision.ConfidenceScore);

            if (signal.Direction != SignalDirection.NO_TRADE)
            {
                var openSignals = await _signalRepo.GetOpenSignalsAsync(symbol, ct);

                // Deduplicate: block exact same bar+TF repeat
                if (openSignals.Any(s => s.Timeframe == tf.Name && s.SignalTimeUtc == signal.SignalTimeUtc))
                {
                    var dedupDecision = decision with
                    {
                        LifecycleState = SignalLifecycleState.RISK_BLOCKED,
                        FinalBlockReason = "Duplicate signal: same bar+TF already open"
                    };
                    await _decisionAuditRepo.InsertDecisionAsync(dedupDecision, ct);
                    await FinalizeMlArtifactsAsync(mlArtifacts, MlEvaluationLinkStatus.OperationallyBlocked, ct);
                    return;
                }

                // Conflict guard: block opposite-direction signal while any open signal exists on this TF
                var oppositeDir = signal.Direction == SignalDirection.BUY ? SignalDirection.SELL : SignalDirection.BUY;
                var conflicting = openSignals.FirstOrDefault(s => s.Timeframe == tf.Name && s.Direction == oppositeDir);
                if (conflicting != null)
                {
                    var conflictDecision = decision with
                    {
                        LifecycleState = SignalLifecycleState.RISK_BLOCKED,
                        FinalBlockReason = $"Conflicting open {conflicting.Direction} signal from {conflicting.SignalTimeUtc:HH:mm}"
                    };
                    await _decisionAuditRepo.InsertDecisionAsync(conflictDecision, ct);
                    _logger.LogInformation(
                        "[{Tf}] Signal {NewDir} blocked — conflicting open {OldDir} signal from {OpenTime} still active",
                        tf.Name, signal.Direction, conflicting.Direction, conflicting.SignalTimeUtc.ToString("HH:mm"));
                    await FinalizeMlArtifactsAsync(mlArtifacts, MlEvaluationLinkStatus.OperationallyBlocked, ct);
                    return;
                }

                // P5-01: Check session-level risk limits (only count outcomes from current strategy version)
                var todayStart = DateTimeOffset.UtcNow.Date;
                var todayEnd = todayStart.AddDays(1);
                var todayOutcomes = await _signalRepo.GetOutcomesAsync(symbol,
                    new DateTimeOffset(todayStart, TimeSpan.Zero),
                    new DateTimeOffset(todayEnd, TimeSpan.Zero),
                    p.StrategyVersion, ct);
                var sessionBlock = RiskManager.CheckSessionLimits(riskPolicy, openSignals.Count, todayOutcomes);
                if (sessionBlock != null)
                {
                    var sessionDecision = decision with
                    {
                        LifecycleState = SignalLifecycleState.SESSION_BLOCKED,
                        FinalBlockReason = sessionBlock,
                        OutcomeCategory = OutcomeCategory.OPERATIONAL_BLOCKED,
                        ReasonCodes = decision.ReasonCodes.Concat(new[] { RejectReasonCode.SESSION_LIMIT_REACHED }).Distinct().ToList(),
                        ReasonDetails = decision.ReasonDetails.Concat(new[] { $"Session limit: {sessionBlock}" }).ToList()
                    };
                    await _decisionAuditRepo.InsertDecisionAsync(sessionDecision, ct);
                    _logger.LogInformation("[{Tf}] Signal {Dir} blocked by session limits: {Reason}",
                        tf.Name, signal.Direction, sessionBlock);
                    await FinalizeMlArtifactsAsync(mlArtifacts, MlEvaluationLinkStatus.OperationallyBlocked, ct);
                    return;
                }

                // ─── Structure-aware exit computation ────────────────
                decimal swingExtreme;
                if (closedHistory.Count >= 5)
                {
                    var recentCandles = closedHistory.TakeLast(5);
                    swingExtreme = signal.Direction == SignalDirection.BUY
                        ? recentCandles.Min(c => c.MidLow)
                        : recentCandles.Max(c => c.MidHigh);
                }
                else
                {
                    swingExtreme = signal.Direction == SignalDirection.BUY
                        ? closedCandle.MidLow
                        : closedCandle.MidHigh;
                }

                decimal spreadPct = snap.CloseMid > 0
                    ? snap.Spread / snap.CloseMid : 0;
                var estimatedEntry = RiskManager.EstimateLiveFillPrice(
                    signal.Direction,
                    snap.CloseMid,
                    spreadPct,
                    p.LiveEntrySlippageBufferPct);

                // Build structure levels from candle history
                var structureLevels = closedHistory.Count >= p.ExitStructureLookbackBars
                    ? StructureAnalyzer.Analyze(closedHistory, estimatedEntry)
                    : closedHistory.Count >= 7
                        ? StructureAnalyzer.Analyze(closedHistory, estimatedEntry, shoulderBars: 2)
                        : null;

                var exitCtx = new ExitEngine.ExitContext
                {
                    Direction = signal.Direction,
                    EntryPrice = estimatedEntry,
                    Atr = snap.Atr14,
                    SpreadPct = spreadPct,
                    ConfidenceScore = signal.ConfidenceScore,
                    Regime = signal.Regime,
                    Timeframe = tf.Name,
                    Structure = structureLevels,
                    SwingExtreme = swingExtreme
                };
                var exitPolicy = ExitEngine.BuildPolicy(p);
                var exitResult = ExitEngine.Compute(exitCtx, exitPolicy);

                // Risk USD (from legacy path — kept for position sizing)
                decimal riskUsd = p.AccountBalanceUsd * p.RiskPerTradePercent / 100m;

                if (exitResult.Allowed)
                {
                    var fullSignal = signal with
                    {
                        EntryPrice = estimatedEntry,
                        TpPrice = exitResult.TakeProfit,
                        SlPrice = exitResult.StopLoss,
                        RiskUsd = riskUsd,
                        RiskPercent = p.RiskPerTradePercent,
                        Tp1Price = exitResult.Tp1,
                        Tp2Price = exitResult.Tp2,
                        Tp3Price = exitResult.Tp3,
                        RiskRewardRatio = exitResult.RiskRewardRatio,
                        ExitModel = exitResult.ExitModel,
                        ExitExplanation = exitResult.Explanation,
                        MarketConditionClass = currentConditionClass?.ToKey(),
                        EvaluationId = mlArtifacts?.EvaluationId ?? decision.EvaluationId
                    };
                    await _signalRepo.InsertSignalAsync(fullSignal, ct);

                    // FR-1: Update decision to PERSISTED now that the signal was accepted and stored
                    var persistedDecision = decision with
                    {
                        LifecycleState = SignalLifecycleState.PERSISTED
                    };
                    await _decisionAuditRepo.InsertDecisionAsync(persistedDecision, ct);

                    NotifyTelegramInBackground(
                        TelegramMessageFormatter.NewSignal(fullSignal, persistedDecision, _currentRegimeResult, p.OutcomeTimeoutBars),
                        ct);

                    await LinkMlArtifactsAsync(mlArtifacts, fullSignal.SignalId, ct);

                    // P6-01: Persist signal features
                    await InsertSignalFeaturesAsync(fullSignal, snap, closedCandle, ct);

                    _lastSignal = fullSignal;
                    _barsSinceLastSignal = 0;
                    _logger.LogInformation("[{Tf}] Signal generated: {Dir} @ {Entry} TP={TP} SL={SL} Risk={Risk}% Score={Score}",
                        tf.Name, fullSignal.Direction, fullSignal.EntryPrice, fullSignal.TpPrice,
                        fullSignal.SlPrice, fullSignal.RiskPercent, fullSignal.ConfidenceScore);
                }
                else
                {
                    var riskBlockedDecision = decision with
                    {
                        DecisionType = SignalDirection.NO_TRADE,
                        OutcomeCategory = OutcomeCategory.OPERATIONAL_BLOCKED,
                        LifecycleState = SignalLifecycleState.RISK_BLOCKED,
                        FinalBlockReason = exitResult.RejectReason ?? "Exit engine rejected",
                        ReasonCodes = decision.ReasonCodes.Concat(new[] { RejectReasonCode.RISK_MANAGER_BLOCKED }).Distinct().ToList(),
                        ReasonDetails = decision.ReasonDetails.Concat(new[]
                        {
                            $"Exit engine blocked signal: {exitResult.RejectReason ?? "Unknown"}"
                        }).ToList()
                    };

                    await _decisionAuditRepo.InsertDecisionAsync(riskBlockedDecision, ct);
                    _lastDecision = riskBlockedDecision;

                    _logger.LogInformation("[{Tf}] Signal {Dir} rejected by exit engine: {Reason}",
                        tf.Name, signal.Direction, exitResult.RejectReason ?? "N/A");
                    await FinalizeMlArtifactsAsync(mlArtifacts, riskBlockedDecision, ct);
                }
            }
            else
            {
                await FinalizeMlArtifactsAsync(mlArtifacts, decision, ct);
            }

            _barsSinceLastSignal++;
        }
        catch (Exception ex)
        {
            if (mlArtifacts != null)
                await FinalizeMlArtifactsAsync(mlArtifacts, MlEvaluationLinkStatus.NoSignalExpected, ct);
            await RecordEvaluationExceptionAsync(symbol, tf, closedCandle.OpenTime, DecisionOrigin.CLOSED_BAR, ex, ct);
        }
        finally
        {
            try
            {
                await EvaluateOpenSignalsAsync(symbol, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Open signal evaluation failed after signal-generation cycle");
            }
        }
    }

    /// <summary>
    /// Evaluate signal on every 1m candle close using running (provisional) candles
    /// built from the 1m bars accumulated so far in each higher-TF bucket.
    /// Evaluates ALL signal timeframes (5m, 15m, 30m, 1h, 4h) for maximum scalping opportunities.
    /// </summary>
    private async Task TryEvaluateOn1mClose(string symbol, RichCandle closed1mCandle,
        DateTimeOffset current5mBucket, CancellationToken ct)
    {
        try
        {
            var p = GetRuntimeParameters();

            // TF-4: Check regime freshness
            if (_currentRegimeResult != null)
            {
                var regimeAge = closed1mCandle.OpenTime - _currentRegimeResult.CandleOpenTimeUtc;
                var biasTfMinutes = Math.Max(1, Timeframe.ByName(p.TimeframeBias).Minutes);
                var ageBars = (int)(regimeAge.TotalMinutes / biasTfMinutes);
                if (ageBars > p.MaxRecoveredRegimeAgeBars)
                {
                    _logger.LogInformation(
                        "[1m] Skipping 1m-eval — regime stale ({AgeBars} {BiasTf} bars > max {MaxBars}), last regime @ {RegimeBar}",
                        ageBars, p.TimeframeBias, p.MaxRecoveredRegimeAgeBars,
                        _currentRegimeResult.CandleOpenTimeUtc.ToString("HH:mm"));
                    return;
                }
            }

            // ─── Direct 1m scalp signal evaluation ──────────────────
            if (p.ScalpingEnabled && _currentRegimeResult != null)
            {
                await TryEvaluateScalpSignal(symbol, closed1mCandle, p, ct);
            }

            // ─── Running-candle evaluation on HTF (5m/15m/30m/1h) ──
            // Include 1h so a directional 1h bias can produce signals every minute
            // instead of waiting for the once-per-hour 1h candle close.
            foreach (var tf in new[] { Timeframe.M5, Timeframe.M15, Timeframe.M30, Timeframe.H1 })
            {
                await TryEvaluateRunningCandle(symbol, closed1mCandle, tf, p, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "1m-eval signal evaluation failed");
        }
        finally
        {
            try
            {
                await EvaluateOpenSignalsAsync(symbol, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Open signal evaluation failed after 1m evaluation cycle");
            }
        }
    }

    /// <summary>
    /// Evaluate a scalp signal directly on the 1m timeframe using closed 1m candle history.
    /// Uses tighter parameters: higher confidence threshold, lower ATR minimum, faster R:R.
    /// </summary>
    private async Task TryEvaluateScalpSignal(string symbol, RichCandle closed1mCandle,
        StrategyParameters p, CancellationToken ct)
    {
        MlEvaluationArtifacts? scalpMlArtifacts = null;
        try
        {
            _logger.LogInformation("[LOG-TRACE] TryEvaluateScalpSignal: symbol={Symbol} barTime={BarTime} close={Close} vol={Vol}", symbol, closed1mCandle.OpenTime.ToString("o"), closed1mCandle.MidClose, closed1mCandle.Volume);
            // Cooldown: skip if too soon after last 1m scalp signal
            if (_lastScalpBarTime.HasValue)
            {
                var barsSinceLast = (int)((closed1mCandle.OpenTime - _lastScalpBarTime.Value).TotalMinutes);
                if (barsSinceLast < p.ScalpCooldownBars)
                {
                    _logger.LogInformation("[1m] Scalp cooldown active: {Bars}/{Required} bars since last scalp @ {LastBar}",
                        barsSinceLast, p.ScalpCooldownBars, _lastScalpBarTime.Value.ToString("HH:mm"));
                    return;
                }
            }

            // Get enough closed 1m candles for indicator warm-up.
            // ScalpWarmUpBars is the scalp-specific minimum; WarmUpPeriod is the indicator
            // warm-up floor (RSI/MACD need enough history). Fetch the larger of the two.
            var requiredBars = Math.Max(p.ScalpWarmUpBars, p.WarmUpPeriod) + 10;
            var closed1m = await _repo.GetClosedCandlesAsync(Timeframe.M1, symbol, requiredBars, ct);
            if (closed1m.Count < p.ScalpWarmUpBars)
            {
                _logger.LogDebug("[1m] Scalp skipped — only {Count} closed 1m candles, need {Need} (ScalpWarmUpBars)",
                    closed1m.Count, p.ScalpWarmUpBars);
                return;
            }

            // Compute 1m indicators
            var snapshot = IndicatorEngine.ComputeLatest(symbol, Timeframe.M1.Name, closed1m, p);
            if (snapshot == null)
            {
                _logger.LogDebug("[1m] Scalp skipped — IndicatorEngine returned null snapshot");
                return;
            }

            // T3-1: Block scalp signals on provisional indicator data. Provisional snapshots
            // are produced when closed1m has fewer than WarmUpPeriod bars; indicators like RSI
            // and MACD are statistically unreliable and produce false signals.
            if (snapshot.IsProvisional)
            {
                _logger.LogDebug("[1m] Scalp skipped — snapshot is provisional (insufficient history: {Count} bars)",
                    closed1m.Count);
                return;
            }

            // Scalp ATR gate (1m ATR is naturally smaller)
            if (snapshot.Atr14 < p.ScalpMinAtr)
            {
                _logger.LogInformation("[1m] Scalp skipped — ATR({Atr:F3}) < ScalpMinAtr({Min:F3}) @ {Bar}",
                    snapshot.Atr14, p.ScalpMinAtr, closed1mCandle.OpenTime.ToString("HH:mm"));
                return;
            }

            var prevSnap = _latestSnaps.GetValueOrDefault(Timeframe.M1.Name);
            _prevSnaps[Timeframe.M1.Name] = prevSnap;
            _latestSnaps[Timeframe.M1.Name] = snapshot;

            // MTF confluence: scalp 1m entries align with the highest directional regime
            // found at or above 1m. When intraday TFs (5m/15m) are choppy but 1h/4h is
            // BULLISH, scalps in the bullish direction are still valid pullback entries.
            var scalpRegime = GetBiasRegimeFor(Timeframe.M1);

            // FR-13: Apply adaptive parameter overlay for scalp signals
            var baseParams = p;
            MarketConditionClass? scalpConditionClass = null;
            string? scalpAdaptedJson = null;
            {
                var recentSnaps = _recentSnapBuffer.Get(Timeframe.M1.Name);
                var (adaptedParams, conditionClass) = _adaptiveService.AdaptParameters(
                    p, snapshot, _currentRegimeResult, closed1mCandle, Timeframe.M1, recentSnaps);
                p = adaptedParams;
                scalpConditionClass = conditionClass;
                scalpAdaptedJson = MarketAdaptiveParameterService.BuildOverlayDiffsJson(baseParams, p);
            }

            // Evaluate signal using the standard engine
            var (signal, decision) = SignalEngine.EvaluateWithDecision(
                symbol, scalpRegime, snapshot, prevSnap, closed1mCandle, p,
                SourceMode.LIVE, p.StrategyVersion, evaluationTf: Timeframe.M1);

            if (signal.Direction == SignalDirection.NO_TRADE)
            {
                // Still persist the decision audit for visibility
                if (decision != null)
                {
                    decision = decision with { Origin = DecisionOrigin.SCALP_1M };
                    await _decisionAuditRepo.InsertDecisionAsync(decision, ct);
                }
                return;
            }

            // Apply scalp-specific confidence threshold (higher bar)
            if (signal.ConfidenceScore < p.ScalpConfidenceThreshold)
            {
                _logger.LogDebug("[1m] Scalp signal {Dir} score {Score} below scalp threshold {Threshold}",
                    signal.Direction, signal.ConfidenceScore, p.ScalpConfidenceThreshold);
                decision = decision with { Origin = DecisionOrigin.SCALP_1M };
                await _decisionAuditRepo.InsertDecisionAsync(decision, ct);
                return;
            }

            // FR-1: Stamp origin for all scalp decisions from here on
            decision = decision with
            {
                Origin = DecisionOrigin.SCALP_1M,
                MarketConditionClass = scalpConditionClass?.ToKey(),
                AdaptedParametersJson = scalpAdaptedJson,
                EffectiveRuntimeParametersJson = p.ToJson()
            };

            // Deduplicate: one signal per 1m bucket/direction
            var scalpKey = $"1m|{closed1mCandle.OpenTime:O}|{signal.Direction}";
            if (_provisionalSignalKeys.Contains(scalpKey)) return;

            // Run ML inference before operational/session gates so blocked scalp
            // decisions still get a persisted feature snapshot and can contribute
            // to ML diagnostics / trainer counts.
            scalpMlArtifacts = await RunMlInferenceAsync(
                symbol, snapshot, prevSnap, closed1mCandle, scalpRegime,
                signal.Direction, signal.ConfidenceScore, p, Timeframe.M1, ct);

            decision = decision with
            {
                EvaluationId = scalpMlArtifacts?.EvaluationId ?? decision.EvaluationId
            };

            // Check open positions — use scoped capacity so MaxOpenPerTimeframe / MaxOpenPerDirection
            // rules are respected for scalp signals (same as the HTF closed-bar path).
            var openSignals = await _signalRepo.GetOpenSignalsAsync(symbol, ct);
            var riskPolicy = p.ToRiskPolicy();
            var (capacityCode, capacityReason) = RiskManager.CheckScopedCapacity(
                p, openSignals, Timeframe.M1.Name, signal.Direction);
            if (capacityCode.HasValue)
            {
                var capacityDecision = decision with
                {
                    LifecycleState = SignalLifecycleState.RISK_BLOCKED,
                    FinalBlockReason = capacityReason
                };
                await _decisionAuditRepo.InsertDecisionAsync(capacityDecision, ct);
                await FinalizeMlArtifactsAsync(scalpMlArtifacts, MlEvaluationLinkStatus.OperationallyBlocked, ct);
                return;
            }

            // Persist decision
            if (decision != null)
                await _decisionAuditRepo.InsertDecisionAsync(decision, ct);
            _lastDecision = decision;

            // Session risk limits
            var todayStart = DateTimeOffset.UtcNow.Date;
            var todayEnd = todayStart.AddDays(1);
            var todayOutcomes = await _signalRepo.GetOutcomesAsync(symbol,
                new DateTimeOffset(todayStart, TimeSpan.Zero),
                new DateTimeOffset(todayEnd, TimeSpan.Zero),
                p.StrategyVersion, ct);
            var sessionBlock = RiskManager.CheckSessionLimits(riskPolicy, openSignals.Count, todayOutcomes, isScalp: true);
            if (sessionBlock != null)
            {
                var sessionDecision = decision! with
                {
                    LifecycleState = SignalLifecycleState.SESSION_BLOCKED,
                    FinalBlockReason = sessionBlock
                };
                await _decisionAuditRepo.InsertDecisionAsync(sessionDecision, ct);
                _logger.LogInformation("[1m] Scalp signal {Dir} blocked by session limits: {Reason}",
                    signal.Direction, sessionBlock);
                await FinalizeMlArtifactsAsync(scalpMlArtifacts, MlEvaluationLinkStatus.OperationallyBlocked, ct);
                return;
            }

            // ─── Structure-aware exit computation (scalp) ────────
            decimal swingExtreme;
            if (closed1m.Count >= 5)
            {
                var recentCandles = closed1m.TakeLast(5);
                swingExtreme = signal.Direction == SignalDirection.BUY
                    ? recentCandles.Min(c => c.MidLow)
                    : recentCandles.Max(c => c.MidHigh);
            }
            else
            {
                swingExtreme = signal.Direction == SignalDirection.BUY
                    ? closed1mCandle.MidLow
                    : closed1mCandle.MidHigh;
            }

            decimal spreadPct = snapshot.CloseMid > 0 ? snapshot.Spread / snapshot.CloseMid : 0;
            var estimatedEntry = RiskManager.EstimateLiveFillPrice(
                signal.Direction,
                snapshot.CloseMid,
                spreadPct,
                p.LiveEntrySlippageBufferPct);

            // Build structure levels from 1m candle history
            var scalpStructure = closed1m.Count >= 20
                ? StructureAnalyzer.Analyze(closed1m, estimatedEntry, shoulderBars: 2)
                : null;

            var scalpExitCtx = new ExitEngine.ExitContext
            {
                Direction = signal.Direction,
                EntryPrice = estimatedEntry,
                Atr = snapshot.Atr14,
                SpreadPct = spreadPct,
                ConfidenceScore = signal.ConfidenceScore,
                Regime = signal.Regime,
                Timeframe = Timeframe.M1.Name,
                Structure = scalpStructure,
                SwingExtreme = swingExtreme
            };
            var scalpExitPolicy = ExitEngine.BuildScalpPolicy(p);
            var scalpExitResult = ExitEngine.Compute(scalpExitCtx, scalpExitPolicy);

            decimal riskUsd = p.AccountBalanceUsd * p.RiskPerTradePercent / 100m;

            if (scalpExitResult.Allowed)
            {
                var fullSignal = signal with
                {
                    EntryPrice = estimatedEntry,
                    TpPrice = scalpExitResult.TakeProfit,
                    SlPrice = scalpExitResult.StopLoss,
                    RiskUsd = riskUsd,
                    RiskPercent = p.RiskPerTradePercent,
                    Tp1Price = scalpExitResult.Tp1,
                    Tp2Price = scalpExitResult.Tp2,
                    Tp3Price = scalpExitResult.Tp3,
                    RiskRewardRatio = scalpExitResult.RiskRewardRatio,
                    ExitModel = scalpExitResult.ExitModel,
                    ExitExplanation = scalpExitResult.Explanation
                };
                await _signalRepo.InsertSignalAsync(fullSignal, ct);

                // FR-1: Update decision to PERSISTED
                var persistedDecision = decision! with { LifecycleState = SignalLifecycleState.PERSISTED };
                await _decisionAuditRepo.InsertDecisionAsync(persistedDecision, ct);

                NotifyTelegramInBackground(
                    TelegramMessageFormatter.NewSignal(fullSignal, persistedDecision, _currentRegimeResult, p.OutcomeTimeoutBars),
                    ct);

                await LinkMlArtifactsAsync(scalpMlArtifacts, fullSignal.SignalId, ct);

                await InsertSignalFeaturesAsync(fullSignal, snapshot, closed1mCandle, ct);

                _lastSignal = fullSignal;
                _lastScalpBarTime = closed1mCandle.OpenTime;
                _provisionalSignalKeys.Add(scalpKey);
                _logger.LogInformation(
                    "[1m] Scalp signal generated: {Dir} @ {Entry} TP={TP} SL={SL} Risk={Risk}% Score={Score}",
                    fullSignal.Direction, fullSignal.EntryPrice, fullSignal.TpPrice,
                    fullSignal.SlPrice, fullSignal.RiskPercent, fullSignal.ConfidenceScore);
            }
            else
            {
                _logger.LogInformation("[1m] Scalp signal {Dir} rejected by exit engine: {Reason}",
                    signal.Direction, scalpExitResult.RejectReason ?? "N/A");
                var riskDecision = decision! with
                {
                    LifecycleState = SignalLifecycleState.RISK_BLOCKED,
                    FinalBlockReason = scalpExitResult.RejectReason ?? "Exit engine rejected"
                };
                await _decisionAuditRepo.InsertDecisionAsync(riskDecision, ct);
                await FinalizeMlArtifactsAsync(scalpMlArtifacts, MlEvaluationLinkStatus.OperationallyBlocked, ct);
            }
        }
        catch (Exception ex)
        {
            if (scalpMlArtifacts != null)
                await FinalizeMlArtifactsAsync(scalpMlArtifacts, MlEvaluationLinkStatus.NoSignalExpected, ct);
            await RecordEvaluationExceptionAsync(symbol, Timeframe.M1, closed1mCandle.OpenTime, DecisionOrigin.SCALP_1M, ex, ct);
        }
    }

    /// <summary>
    /// Build a running candle for the given timeframe from accumulated 1m bars and evaluate signal.
    /// </summary>
    private async Task TryEvaluateRunningCandle(string symbol, RichCandle closed1mCandle,
        Timeframe tf, StrategyParameters p, CancellationToken ct)
    {
        MlEvaluationArtifacts? mlArtifacts = null;
        try
        {
            _logger.LogInformation("[LOG-TRACE] TryEvaluateRunningCandle: symbol={Symbol} tf={Tf} barTime={BarTime} close={Close} vol={Vol}", symbol, tf.Name, closed1mCandle.OpenTime.ToString("o"), closed1mCandle.MidClose, closed1mCandle.Volume);
            // Compute the current bucket for this timeframe
            var currentBucket = tf.Floor(closed1mCandle.OpenTime);
            var nextBucket = currentBucket.Add(tf.Duration);

            // Get 1m bars in this TF bucket
            var m1InBucket = await _repo.GetClosedCandlesInRangeAsync(
                Timeframe.M1, symbol, currentBucket, nextBucket, ct);
            if (m1InBucket.Count == 0) return;

            // FR-4: Configurable bar maturity ratio. Fast TFs (≤5m) use RunningCandleMaturityFastTf,
            // slow TFs (15m+) use RunningCandleMaturitySlowTf. This replaces the previous 100%
            // completeness requirement which prevented partial running-candle evaluation entirely.
            decimal maturityRatio = tf.Minutes <= 5
                ? p.RunningCandleMaturityFastTf
                : p.RunningCandleMaturitySlowTf;
            int minBarsRequired = Math.Max(1, (int)Math.Ceiling(tf.Minutes * maturityRatio));
            if (m1InBucket.Count < minBarsRequired) return;

            // If this 1m close *completes* the TF bucket, OnHigherTimeframeClosed already
            // evaluated and persisted a DecisionAudit for the same bar. Running a second
            // evaluation here would produce a duplicate insert (DO NOTHING) and a WRN log
            // on every candle close. Defer entirely to the closed-candle path.
            if (closed1mCandle.OpenTime.Add(Timeframe.M1.Duration) >= nextBucket) return;

            // Aggregate into a single running candle for this TF
            var runningCandles = CandleAggregator.Aggregate(m1InBucket.ToList(), tf);
            if (runningCandles.Count != 1) return;
            var runningCandle = runningCandles[0];

            // T3-9: VWAP midnight-crossing guard.
            // If the 1m bars in this bucket span a UTC midnight boundary, VWAP resets mid-bucket
            // in the real market but our accumulated VWAP uses the bucket open-time (pre-midnight).
            // The resulting Vwap value will be stale/misleading for pullback detection and ML features.
            // Skip the running-candle evaluation in this case; the next fully-intra-day bucket is clean.
            var firstBarDay = DateOnly.FromDateTime(m1InBucket[0].OpenTime.UtcDateTime);
            var lastBarDay = DateOnly.FromDateTime(m1InBucket[^1].OpenTime.UtcDateTime);
            if (firstBarDay != lastBarDay)
            {
                _logger.LogDebug(
                    "[{Tf}] Running candle skipped — bucket spans UTC midnight ({First} → {Last}). " +
                    "VWAP would be stale; next intra-day bucket will be clean.",
                    tf.Name,
                    m1InBucket[0].OpenTime.UtcDateTime.ToString("HH:mm"),
                    m1InBucket[^1].OpenTime.UtcDateTime.ToString("HH:mm"));
                return;
            }

            // Build history by appending the running candle to recent closed candles for this TF
            var closedHistory = await _repo.GetClosedCandlesAsync(tf, symbol, p.WarmUpPeriod + 10, ct);
            if (closedHistory.Count < p.WarmUpPeriod) return;

            var historyWithRunning = closedHistory.ToList();
            historyWithRunning.Add(runningCandle);

            // Compute indicators on the combined history
            var runningSnap = IndicatorEngine.ComputeLatest(symbol, tf.Name, historyWithRunning, p);
            if (runningSnap == null) return;
            var snap = runningSnap;

            var prevSnap = _latestSnaps.GetValueOrDefault(tf.Name);

            // TF-4: Regime freshness gate — same check as closed-candle evaluation.
            // Running candles must not fire on a stale regime (e.g. regime from 3 hours
            // ago while market structure has changed). Reject silently; the audit log
            // for closed candles already captures the staleness.
            if (_currentRegimeResult != null)
            {
                var biasTfMins = Math.Max(1, Timeframe.ByName(p.TimeframeBias).Minutes);
                var regimeAge = runningCandle.OpenTime - _currentRegimeResult.CandleOpenTimeUtc;
                var ageBars = (int)(regimeAge.TotalMinutes / biasTfMins);
                if (ageBars > p.MaxRecoveredRegimeAgeBars)
                {
                    _logger.LogWarning(
                        "[{Tf}] Running candle evaluation skipped — regime stale ({AgeBars} bars > {MaxBars})",
                        tf.Name, ageBars, p.MaxRecoveredRegimeAgeBars);
                    return;
                }
            }

            // MTF confluence: anchor running-candle entries on the highest directional regime
            // found at or above this TF, so a BULLISH 1h bias unblocks 5m/15m/30m running BUYs
            // even when the per-TF regime is NEUTRAL.
            var tfRegime = GetBiasRegimeFor(tf);

            // FR-13: Apply adaptive parameter overlay (same as closed-candle path)
            var baseParams = p;
            MarketConditionClass? currentConditionClass = null;
            string? adaptedParametersJson = null;
            if (runningSnap != null)
            {
                var recentSnaps = _recentSnapBuffer.Get(tf.Name);
                var (adaptedParams, conditionClass) = _adaptiveService.AdaptParameters(
                    p, snap, _currentRegimeResult, runningCandle, tf, recentSnaps);
                p = adaptedParams;
                currentConditionClass = conditionClass;
                adaptedParametersJson = MarketAdaptiveParameterService.BuildOverlayDiffsJson(baseParams, p);
            }

            // Evaluate signal using the running snapshot
            var (preSignal, preDecision) = SignalEngine.EvaluateWithDecision(
                symbol, tfRegime, snap, prevSnap, runningCandle, p,
                SourceMode.LIVE, p.StrategyVersion, evaluationTf: tf);
            mlArtifacts = await RunMlInferenceAsync(
                symbol, snap, prevSnap, runningCandle, tfRegime,
                preSignal.Direction, preSignal.ConfidenceScore, p, tf, ct);
            var mlPrediction = mlArtifacts?.Prediction;
            var (signal, decision) = SignalEngine.EvaluateWithMl(
                symbol, tfRegime, snap, prevSnap, runningCandle, p,
                mlPrediction, _frequencyManager,
                SourceMode.LIVE, p.StrategyVersion, evaluationTf: tf,
                preComputed: (preSignal, preDecision));

            // FR-1/FR-4: Stamp running-candle origin
            // FR-16: Thread EvaluationId for correlation
            // FR-8: Snapshot effective runtime parameters
            decision = decision with
            {
                Origin = DecisionOrigin.PARTIAL_RUNNING,
                MarketConditionClass = currentConditionClass?.ToKey(),
                AdaptedParametersJson = adaptedParametersJson,
                EvaluationId = mlArtifacts?.EvaluationId ?? decision.EvaluationId,
                EffectiveRuntimeParametersJson = p.ToJson()
            };

            await _decisionAuditRepo.InsertDecisionAsync(decision, ct);
            _lastDecision = decision;

            _logger.LogInformation(
                "[{Tf}] 1m-eval SignalDecision symbol={Symbol} barTime={BarTime} regime={Regime} decision={Decision} outcome={Outcome} score={Score} m1Bars={M1Bars}",
                tf.Name, decision.Symbol, decision.BarTimeUtc.ToString("o"),
                decision.UsedRegime?.ToString() ?? "NONE",
                decision.DecisionType, decision.OutcomeCategory,
                decision.ConfidenceScore, m1InBucket.Count);

            // Only persist and act if we get a directional signal (BUY or SELL)
            if (signal.Direction == SignalDirection.NO_TRADE)
            {
                await FinalizeMlArtifactsAsync(mlArtifacts, decision, ct);
                return;
            }

            // Deduplicate provisional signals: only one per TF/bucket/direction
            var provisionalKey = $"{tf.Name}|{runningCandle.OpenTime:O}|{signal.Direction}";
            if (_provisionalSignalKeys.Contains(provisionalKey))
            {
                await FinalizeMlArtifactsAsync(mlArtifacts, MlEvaluationLinkStatus.OperationallyBlocked, ct);
                return;
            }

            // Deduplicate: only 1 signal per TF per candle period
            // SignalTimeUtc = candle close time (openTime + duration), so match exactly
            var openSignals = await _signalRepo.GetOpenSignalsAsync(symbol, ct);
            if (openSignals.Any(s => s.Timeframe == tf.Name && s.SignalTimeUtc == signal.SignalTimeUtc))
            {
                await FinalizeMlArtifactsAsync(mlArtifacts, MlEvaluationLinkStatus.OperationallyBlocked, ct);
                return;
            }

            // Check session-level risk limits
            var riskPolicy = p.ToRiskPolicy();
            if (openSignals.Count >= riskPolicy.MaxOpenPositions)
            {
                var capDecision = decision with
                {
                    LifecycleState = SignalLifecycleState.RISK_BLOCKED,
                    FinalBlockReason = $"Max open positions ({riskPolicy.MaxOpenPositions}) reached"
                };
                await _decisionAuditRepo.InsertDecisionAsync(capDecision, ct);
                await FinalizeMlArtifactsAsync(mlArtifacts, MlEvaluationLinkStatus.OperationallyBlocked, ct);
                return;
            }

            var todayStart = DateTimeOffset.UtcNow.Date;
            var todayEnd = todayStart.AddDays(1);
            var todayOutcomes = await _signalRepo.GetOutcomesAsync(symbol,
                new DateTimeOffset(todayStart, TimeSpan.Zero),
                new DateTimeOffset(todayEnd, TimeSpan.Zero),
                p.StrategyVersion, ct);
            var sessionBlock = RiskManager.CheckSessionLimits(riskPolicy, openSignals.Count, todayOutcomes);
            if (sessionBlock != null)
            {
                var sesDecision = decision with
                {
                    LifecycleState = SignalLifecycleState.SESSION_BLOCKED,
                    FinalBlockReason = sessionBlock
                };
                await _decisionAuditRepo.InsertDecisionAsync(sesDecision, ct);
                _logger.LogInformation("[{Tf}] 1m-eval signal {Dir} blocked by session limits: {Reason}",
                    tf.Name, signal.Direction, sessionBlock);
                await FinalizeMlArtifactsAsync(mlArtifacts, MlEvaluationLinkStatus.OperationallyBlocked, ct);
                return;
            }

            // ─── Structure-aware exit computation (running candle) ────
            decimal swingExtreme;
            if (closedHistory.Count >= 5)
            {
                var recentCandles = closedHistory.TakeLast(5);
                swingExtreme = signal.Direction == SignalDirection.BUY
                    ? recentCandles.Min(c => c.MidLow)
                    : recentCandles.Max(c => c.MidHigh);
            }
            else
            {
                swingExtreme = signal.Direction == SignalDirection.BUY
                    ? runningCandle.MidLow
                    : runningCandle.MidHigh;
            }

            decimal spreadPct = snap.CloseMid > 0
                ? snap.Spread / snap.CloseMid : 0;
            var estimatedEntry = RiskManager.EstimateLiveFillPrice(
                signal.Direction,
                snap.CloseMid,
                spreadPct,
                p.LiveEntrySlippageBufferPct);

            // Build structure levels from candle history
            var rcStructure = closedHistory.Count >= p.ExitStructureLookbackBars
                ? StructureAnalyzer.Analyze(closedHistory, estimatedEntry)
                : closedHistory.Count >= 7
                    ? StructureAnalyzer.Analyze(closedHistory, estimatedEntry, shoulderBars: 2)
                    : null;

            var rcExitCtx = new ExitEngine.ExitContext
            {
                Direction = signal.Direction,
                EntryPrice = estimatedEntry,
                Atr = snap.Atr14,
                SpreadPct = spreadPct,
                ConfidenceScore = signal.ConfidenceScore,
                Regime = signal.Regime,
                Timeframe = tf.Name,
                Structure = rcStructure,
                SwingExtreme = swingExtreme
            };
            var rcExitPolicy = ExitEngine.BuildPolicy(p);
            var rcExitResult = ExitEngine.Compute(rcExitCtx, rcExitPolicy);

            decimal riskUsd = p.AccountBalanceUsd * p.RiskPerTradePercent / 100m;

            if (rcExitResult.Allowed)
            {
                var fullSignal = signal with
                {
                    EntryPrice = estimatedEntry,
                    TpPrice = rcExitResult.TakeProfit,
                    SlPrice = rcExitResult.StopLoss,
                    RiskUsd = riskUsd,
                    RiskPercent = p.RiskPerTradePercent,
                    Tp1Price = rcExitResult.Tp1,
                    Tp2Price = rcExitResult.Tp2,
                    Tp3Price = rcExitResult.Tp3,
                    RiskRewardRatio = rcExitResult.RiskRewardRatio,
                    ExitModel = rcExitResult.ExitModel,
                    ExitExplanation = rcExitResult.Explanation
                };
                await _signalRepo.InsertSignalAsync(fullSignal, ct);

                // FR-1: Mark as PERSISTED
                var persistedDecision = decision with { LifecycleState = SignalLifecycleState.PERSISTED };
                await _decisionAuditRepo.InsertDecisionAsync(persistedDecision, ct);

                await InsertSignalFeaturesAsync(fullSignal, snap, runningCandle, ct);
                await LinkMlArtifactsAsync(mlArtifacts, fullSignal.SignalId, ct);

                _lastSignal = fullSignal;
                _provisionalSignalKeys.Add(provisionalKey);
                _logger.LogInformation("[{Tf}] 1m-eval Signal generated: {Dir} @ {Entry} TP={TP} SL={SL} Risk={Risk}% Score={Score}",
                    tf.Name, fullSignal.Direction, fullSignal.EntryPrice, fullSignal.TpPrice,
                    fullSignal.SlPrice, fullSignal.RiskPercent, fullSignal.ConfidenceScore);
            }
            else
            {
                var riskDecision = decision with
                {
                    LifecycleState = SignalLifecycleState.RISK_BLOCKED,
                    FinalBlockReason = rcExitResult.RejectReason ?? "Exit engine rejected"
                };
                await _decisionAuditRepo.InsertDecisionAsync(riskDecision, ct);
                _logger.LogInformation("[{Tf}] 1m-eval signal {Dir} rejected by exit engine: {Reason}",
                    tf.Name, signal.Direction, rcExitResult.RejectReason ?? "N/A");
                await FinalizeMlArtifactsAsync(mlArtifacts, MlEvaluationLinkStatus.OperationallyBlocked, ct);
            }
        }
        catch (Exception ex)
        {
            if (mlArtifacts != null)
                await FinalizeMlArtifactsAsync(mlArtifacts, MlEvaluationLinkStatus.NoSignalExpected, ct);
            await RecordEvaluationExceptionAsync(symbol, tf, closed1mCandle.OpenTime, DecisionOrigin.PARTIAL_RUNNING, ex, ct);
        }
    }

    /// <summary>
    /// Run ML inference on the current evaluation context and return the prediction.
    /// Also persists feature snapshots and predictions to DB.
    /// Returns null when MlMode=DISABLED or inference is unavailable.
    /// </summary>
    private async Task<MlEvaluationArtifacts?> RunMlInferenceAsync(
        string symbol, IndicatorSnapshot snap, IndicatorSnapshot? prevSnap,
        RichCandle candle, RegimeResult regime, SignalDirection direction,
        int ruleBasedScore, StrategyParameters p, Timeframe evaluationTf, CancellationToken ct)
    {
        if (p.MlMode == MlMode.DISABLED)
        {
            _logger.LogDebug("[ML] MlMode=DISABLED — skipping inference for {Symbol}", symbol);
            return null;
        }

        var recentSnapshots = _recentSnapBuffer.Get(evaluationTf.Name);

        _logger.LogInformation(
            "[ML] Pipeline starting | Symbol={Symbol} | Mode={Mode} | ModelReady={Ready} | " +
            "ModelVersion={Version} | Direction={Dir} | RuleScore={Score} | SnapBuffer={Buf}",
            symbol, p.MlMode, _mlInference.IsReady, _mlInference.ActiveModelVersion ?? "none",
        direction, ruleBasedScore, recentSnapshots.Count);

        try
        {
            // Gather recent outcomes for feature extraction (last 20)
            var recentOutcomes = await _signalRepo.GetOutcomesAsync(
                symbol,
                DateTimeOffset.UtcNow.AddDays(-7),
                DateTimeOffset.UtcNow,
                p.StrategyVersion, ct);
            var last20Outcomes = recentOutcomes
                .OrderByDescending(o => o.EvaluatedAtUtc)
                .Take(20)
                .ToList();

            _logger.LogDebug(
                "[ML] Feature extraction | RecentOutcomes={Count} (WIN={W} LOSS={L}) | BarsSinceSignal={Bars}",
                last20Outcomes.Count,
                last20Outcomes.Count(o => o.OutcomeLabel == OutcomeLabel.WIN),
                last20Outcomes.Count(o => o.OutcomeLabel == OutcomeLabel.LOSS),
                _barsSinceLastSignal);

            var featureWindowBars = 10;
            var timeframeMinutes = Math.Max(1, evaluationTf.Minutes);
            var recentSignalsWindowStart = snap.CandleOpenTimeUtc.AddMinutes(-(featureWindowBars * timeframeMinutes));
            var recentSignals = await _signalRepo.GetRecentSignalsAsync(
                symbol,
                evaluationTf.Name,
                recentSignalsWindowStart,
                snap.CandleOpenTimeUtc,
                featureWindowBars,
                ct);
            var btcContext = _btcContextProvider.GetCurrentContext();

            // Build feature vector
            var features = MlFeatureExtractor.Extract(
                snap, prevSnap,
                recentSnapshots,
                candle, regime, direction,
                ruleBasedScore, last20Outcomes,
                _barsSinceLastSignal, evaluationTf,
                recentSignals,
                btcContext);

            _logger.LogDebug(
                "[ML] Feature vector built | EvalId={EvalId} | FeatureCount={Count}",
                features.EvaluationId, features.ToFloatArray().Length);

            // Run inference
            var prediction = _mlInference.Predict(features, p.MlMode);

            if (prediction == null)
            {
                _logger.LogWarning(
                    "[ML] Predict() returned null | Symbol={Symbol} | Mode={Mode} | " +
                    "ModelReady={Ready} — no prediction stored this bar",
                    symbol, p.MlMode, _mlInference.IsReady);
            }
            else
            {
                _logger.LogInformation(
                    "[ML] Prediction ready | RawWinProb={RawWinProb:P1} | CalibratedWinProb={CalWinProb:P1} | PredConf={Conf} | " +
                    "ExpValueR={EV:F3} | LatencyUs={Lat} | IsActive={Active}",
                    prediction.RawWinProbability, prediction.CalibratedWinProbability, prediction.PredictionConfidence,
                    prediction.ExpectedValueR, prediction.InferenceLatencyUs, prediction.IsActive);
            }

            // Persist feature snapshot
            await _mlFeatureRepo.InsertAsync(
                features,
                signalId: null,
                featureVersion: MlFeatureExtractor.FeatureVersion,
                linkStatus: MlEvaluationLinkStatus.Pending,
                ct: ct);
            _logger.LogDebug("[ML] Feature snapshot persisted | EvalId={EvalId}", features.EvaluationId);

            if (prediction != null)
            {
                await _mlPredictionRepo.InsertAsync(prediction, ct);
                _logger.LogDebug("[ML] Prediction persisted | PredId={PredId}", prediction.PredictionId);
            }

            return new MlEvaluationArtifacts(features.EvaluationId, prediction);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[ML] Pipeline exception | Symbol={Symbol} | Mode={Mode} — falling back to rule-based",
                symbol, p.MlMode);
            return null;
        }
    }

    private async Task LinkMlArtifactsAsync(MlEvaluationArtifacts? artifacts, Guid signalId, CancellationToken ct)
    {
        if (artifacts == null)
            return;

        await _mlFeatureRepo.LinkSignalAsync(artifacts.EvaluationId, signalId, ct);
        if (artifacts.Prediction != null)
            await _mlPredictionRepo.UpdateSignalIdAsync(artifacts.EvaluationId, signalId, ct);

        _logger.LogDebug(
            "[ML] Feature+prediction linked | EvalId={EvalId} → SignalId={SigId}",
            artifacts.EvaluationId, signalId);
    }

    private async Task FinalizeMlArtifactsAsync(
        MlEvaluationArtifacts? artifacts,
        SignalDecision decision,
        CancellationToken ct)
    {
        if (artifacts == null)
            return;

        var status = decision.ReasonCodes.Contains(RejectReasonCode.ML_GATE_FAILED)
            ? MlEvaluationLinkStatus.MlFiltered
            : decision.OutcomeCategory == OutcomeCategory.OPERATIONAL_BLOCKED
                ? MlEvaluationLinkStatus.OperationallyBlocked
                : MlEvaluationLinkStatus.NoSignalExpected;

        await FinalizeMlArtifactsAsync(artifacts, status, ct);
    }

    private async Task FinalizeMlArtifactsAsync(
        MlEvaluationArtifacts? artifacts,
        string status,
        CancellationToken ct)
    {
        if (artifacts == null)
            return;

        await _mlFeatureRepo.UpdateLinkStatusAsync(artifacts.EvaluationId, status, ct);
    }

    /// <summary>
    /// Track indicator snapshot in the rolling buffer for ML feature extraction.
    /// Keeps up to 20 most recent snapshots (newest first).
    /// </summary>
    private void TrackSnapshotForMl(string timeframe, IndicatorSnapshot snap)
    {
        _recentSnapBuffer.Add(timeframe, snap);
    }

    private StrategyParameters GetRuntimeParameters()
    {
        var p = _paramProvider.GetActive();
        // UiPriceOnly mode uses synthetic volume, so volume weight should not influence confidence.
        if (_uiPriceOnly && p.WeightVolume != 0)
            return p with { WeightVolume = 0 };
        return p;
    }

    private async Task RefreshIndicatorCacheAsync(string symbol, StrategyParameters p, CancellationToken ct)
    {
        var toPersist = new List<IndicatorSnapshot>();
        foreach (var tf in new[] { Timeframe.M1, Timeframe.M5, Timeframe.M15, Timeframe.M30, Timeframe.H1, Timeframe.H4 })
        {
            try
            {
                var closed = await _repo.GetClosedCandlesAsync(tf, symbol, p.WarmUpPeriod + 10, ct);
                var allCandles = closed.ToList();

                // 1m timeframe tracks its running candle separately in _openCandle1m
                RichCandle? running = tf == Timeframe.M1
                    ? _openCandle1m
                    : (_openCandlesHTF.TryGetValue(tf, out var htfOpen) ? htfOpen : null);
                bool hasRunning = running != null;
                if (hasRunning) allCandles.Add(running!);

                if (allCandles.Count < p.WarmUpPeriod)
                    continue;

                var snapshot = IndicatorEngine.ComputeLatest(symbol, tf.Name, allCandles, p);
                if (snapshot == null)
                    continue;

                _marketState.UpdateIndicator(tf.Name, snapshot);

                // Do NOT persist provisional (mid-candle) indicator snapshots to DB.
                // Persisting them corrupts historical indicator data used by ML feature
                // export and future regime/signal analysis. Market state cache (above) is
                // sufficient for real-time display; DB is source-of-truth for closed bars only.
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Indicator cache refresh failed for {Tf}", tf.Name);
            }
        }

        if (toPersist.Count > 0)
            await _indicatorRepo.BulkUpsertAsync(toPersist, ct);
    }

    private async Task InsertSignalFeaturesAsync(SignalRecommendation signal, IndicatorSnapshot snap,
        RichCandle candle, CancellationToken ct)
    {
        try
        {
            await _signalRepo.InsertSignalFeaturesAsync(signal.SignalId, new Dictionary<string, decimal>
            {
                ["ema20_minus_ema50"] = snap.Ema20 - snap.Ema50,
                ["rsi14"] = snap.Rsi14,
                ["macd_hist"] = snap.MacdHist,
                ["adx14"] = snap.Adx14,
                ["plus_di_minus_minus_di"] = snap.PlusDi - snap.MinusDi,
                ["distance_to_vwap"] = snap.CloseMid > 0 ? (snap.CloseMid - snap.Vwap) / snap.CloseMid : 0,
                ["volume_ratio"] = snap.VolumeSma20 > 0 ? candle.Volume / snap.VolumeSma20 : 0,
                ["spread_pct"] = snap.CloseMid > 0 ? snap.Spread / snap.CloseMid : 0,
                ["atr_pct"] = snap.CloseMid > 0 ? snap.Atr14 / snap.CloseMid : 0,
                ["body_ratio"] = SignalEngine.ComputeBodyRatio(candle),
                ["hour_of_day"] = candle.OpenTime.UtcDateTime.Hour,
                ["regime_score"] = _currentRegimeResult?.RegimeScore ?? 0
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to insert signal features for {SignalId}", signal.SignalId);
        }
    }

    private void NotifyTelegramInBackground(string message, CancellationToken ct)
    {
        _telegram.SendAsync(message, ct).ContinueWith(
            t => _logger.LogWarning(t.Exception, "[Telegram] Signal notification failed"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private async Task RecordEvaluationExceptionAsync(
        string symbol,
        Timeframe tf,
        DateTimeOffset barTimeUtc,
        DecisionOrigin origin,
        Exception ex,
        CancellationToken ct)
    {
        Interlocked.Increment(ref _missedSignalDueToExceptionCount);
        _lastEvaluationExceptionUtc = DateTimeOffset.UtcNow;

        _marketState.RecordError($"Signal evaluation exception [{tf.Name}] {ex.Message}");
        _logger.LogError(ex,
            "[{Tf}] Signal evaluation failed; audit record persisted and opportunity marked as missed",
            tf.Name);

        var decision = new SignalDecision
        {
            Symbol = symbol,
            Timeframe = tf.Name,
            DecisionTimeUtc = DateTimeOffset.UtcNow,
            BarTimeUtc = barTimeUtc,
            DecisionType = SignalDirection.NO_TRADE,
            OutcomeCategory = OutcomeCategory.EVALUATION_ERROR,
            LifecycleState = SignalLifecycleState.EVALUATED,
            FinalBlockReason = ex.Message,
            Origin = origin,
            UsedRegime = _currentRegimeResult?.Regime,
            UsedRegimeTimestamp = _currentRegimeResult?.CandleOpenTimeUtc,
            ReasonCodes = [RejectReasonCode.EVALUATION_EXCEPTION],
            ReasonDetails = [$"[{tf.Name}] {ex.GetType().Name}: {ex.Message}"],
            IndicatorSnapshot = new Dictionary<string, decimal>(),
            ParameterSetId = _paramProvider.GetActive().StrategyVersion,
            SourceMode = SourceMode.LIVE
        };

        try
        {
            await _decisionAuditRepo.InsertDecisionAsync(decision, ct);
            _lastDecision = decision;
        }
        catch (Exception auditEx)
        {
            _logger.LogError(auditEx,
                "[{Tf}] Failed to persist evaluation-error audit record after signal exception",
                tf.Name);
        }
    }

    private RichCandle MakeCandle(DateTimeOffset openTime, SpotPrice spot) => new()
    {
        OpenTime = openTime,
        BidOpen = spot.Bid,
        BidHigh = spot.Bid,
        BidLow = spot.Bid,
        BidClose = spot.Bid,
        AskOpen = spot.Ask,
        AskHigh = spot.Ask,
        AskLow = spot.Ask,
        AskClose = spot.Ask,
        Volume = 0m,
        BuyerPct = _sentiment.BuyerPct,
        SellerPct = _sentiment.SellerPct,
        SourceTimestampUtc = spot.Timestamp, // B-12: use broker timestamp
        ReceivedTimestampUtc = DateTimeOffset.UtcNow,
        IsClosed = false
    };

    private RichCandle UpdateCandle(RichCandle current, SpotPrice spot, decimal? volume = null) => current with
    {
        BidHigh = Math.Max(current.BidHigh, spot.Bid),
        BidLow = Math.Min(current.BidLow, spot.Bid),
        BidClose = spot.Bid,
        AskHigh = Math.Max(current.AskHigh, spot.Ask),
        AskLow = Math.Min(current.AskLow, spot.Ask),
        AskClose = spot.Ask,
        Volume = volume ?? current.Volume,
        BuyerPct = _sentiment.BuyerPct,
        SellerPct = _sentiment.SellerPct,
        SourceTimestampUtc = spot.Timestamp, // B-12: use broker timestamp
        ReceivedTimestampUtc = DateTimeOffset.UtcNow
    };

    private void PrintHeader(string symbol)
    {
        Console.WriteLine();
        Console.WriteLine($"  {symbol} Live Candles (Ctrl+C to stop)");
        Console.WriteLine($"  Source: {_tickProvider.Kind} | " +
                          $"Rate: {_tickProvider.TickRateHz:F1} Hz | " +
                          $"Healthy: {_tickProvider.IsHealthy}");
        Console.WriteLine();
        Console.WriteLine("  TF     BidH       BidL       AskH       AskL       MidC       Vol       Buy%   Sell%  Candle Time");
        Console.WriteLine("  ─────  ─────────  ─────────  ─────────  ─────────  ─────────  ────────  ─────  ─────  ───────────────────");
    }

    private async Task EvaluateOpenSignalsAsync(string symbol, CancellationToken ct)
    {
        var p = _paramProvider.GetActive();
        var openSignals = await _signalRepo.GetOpenSignalsAsync(symbol, ct);
        foreach (var sig in openSignals)
        {
            // Use the signal's own timeframe for outcome evaluation (not always M5)
            var sigTf = Timeframe.ByName(sig.Timeframe);
            var candlesAfter = await _repo.GetClosedCandlesAfterAsync(sigTf, symbol, sig.SignalTimeUtc, p.OutcomeTimeoutBars, ct);
            if (candlesAfter.Count == 0) continue;

            IReadOnlyList<RichCandle>? intrabarCandles = null;
            if (sigTf != Timeframe.M1)
            {
                var intrabarEnd = candlesAfter[^1].OpenTime.Add(sigTf.Duration);
                intrabarCandles = await _repo.GetClosedCandlesInRangeAsync(
                    Timeframe.M1,
                    symbol,
                    candlesAfter[0].OpenTime,
                    intrabarEnd,
                    ct);
            }

            var outcome = OutcomeEvaluator.Evaluate(sig, candlesAfter, p, sigTf, intrabarCandles,
                intrabarCandles != null ? Timeframe.M1 : null);
            if (outcome.OutcomeLabel == OutcomeLabel.PENDING) continue;

            await _signalRepo.InsertOutcomeAsync(outcome, ct);
            await _signalRepo.UpdateSignalStatusAsync(sig.SignalId,
                outcome.OutcomeLabel == OutcomeLabel.EXPIRED ? SignalStatus.EXPIRED : SignalStatus.CLOSED, ct);

            // M-05: Feed drift detector with resolved outcome + ML prediction
            if (outcome.OutcomeLabel is OutcomeLabel.WIN or OutcomeLabel.LOSS && sig.SignalId != Guid.Empty)
            {
                try
                {
                    var prediction = await _mlPredictionRepo.GetBySignalIdAsync(sig.SignalId, ct);
                    if (prediction != null)
                        _mlDriftDetector.RecordOutcome(prediction.CalibratedWinProbability, outcome.OutcomeLabel == OutcomeLabel.WIN);
                }
                catch { /* non-fatal */ }
            }

            // M-04: Feed adaptive parameter service with resolved outcome
            // Issue #8: Include EXPIRED outcomes so high-expiration conditions are penalized
            if (outcome.OutcomeLabel is OutcomeLabel.WIN or OutcomeLabel.LOSS or OutcomeLabel.EXPIRED)
            {
                try
                {
                    var currentParams = GetRuntimeParameters();
                    _adaptiveService.RecordOutcome(outcome, sig.MarketConditionClass, currentParams);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "MarketAdaptiveParameterService.RecordOutcome failed");
                }
            }

            _logger.LogInformation("[{Tf}] Signal {Id} resolved: {Outcome} PnL={PnlR:F2}R",
                sigTf.Name, sig.SignalId, outcome.OutcomeLabel, outcome.PnlR);
        }
    }

    private void PrintCandles(string symbol)
    {
        Console.Write("\x1b[5A");
        Console.Write("\x1b[2K");
        Console.WriteLine($"  Regime: {_currentRegimeResult?.Regime.ToString() ?? "UNKNOWN"}");

        if (_openCandle1m != null)
        {
            var c = _openCandle1m;
            Console.Write("\x1b[2K");
            Console.WriteLine(
                $"  1m     {c.BidHigh,-9:F2}  {c.BidLow,-9:F2}  {c.AskHigh,-9:F2}  {c.AskLow,-9:F2}  {c.MidClose,-9:F2}  {c.Volume,-8:F0}  {c.BuyerPct,5:F1}  {c.SellerPct,5:F1}  {c.OpenTime:yyyy-MM-dd HH:mm}");
        }
        else
        {
            Console.Write("\x1b[2K\n");
        }

        // Show latest spot for 5m/15m context
        if (_marketState.LatestSpot != null)
        {
            Console.Write("\x1b[2K");
            var ls = _marketState.LatestSpot!;
            Console.WriteLine($"  Spot   Mid={ls.Mid:F2}  Bid={ls.Bid:F2}  Ask={ls.Ask:F2}  Spread={(ls.Ask - ls.Bid):F2}");
        }
        else
        {
            Console.Write("\x1b[2K\n");
        }

        Console.Write("\x1b[2K");
        if (_lastSignal != null)
            Console.WriteLine($"  Signal: {_lastSignal.Direction} @ {_lastSignal.EntryPrice:F2} TP={_lastSignal.TpPrice:F2} SL={_lastSignal.SlPrice:F2} Score={_lastSignal.ConfidenceScore}");
        else
            Console.WriteLine("  Signal: --");

        Console.Write("\x1b[2K");
        Console.WriteLine($"  Tick #{_tickCount}  {DateTimeOffset.UtcNow:HH:mm:ss}");
    }
}
