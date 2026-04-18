using System.Collections.Concurrent;
using System.Text.Json;
using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using Microsoft.Extensions.Logging;

namespace EthSignal.Infrastructure.Engine.ML;

/// <summary>
/// Market-adaptive parameter service that classifies market conditions and applies
/// per-condition parameter overlays.
///
/// Two modes of adaptation:
///   1. Proactive: on every evaluation, classifies market conditions and applies
///      deterministic overlays based on observable indicators (immediate).
///   2. Retrospective: refines overlays using rolling outcome windows per *coarse*
///      condition class (Volatility × Trend) so reachability is realistic.
///
/// Adapted parameters are per-evaluation (transient). Base parameters in the DB
/// are never mutated. Outcome windows and retrospective overlays are persisted to
/// DB so they survive process restarts.
/// </summary>
public sealed class MarketAdaptiveParameterService
{
    private readonly ILogger<MarketAdaptiveParameterService> _logger;
    private readonly AdaptiveParameterLogRepository? _logRepo;
    private readonly IAdaptiveStateRepository? _stateRepo;
    private readonly IParameterRepository? _paramRepo;

    // Per-coarse-condition outcome tracking for retrospective refinement.
    // Coarse key (Volatility_Trend) gives 12 buckets and is reachable in practice.
    private readonly ConcurrentDictionary<string, OutcomeWindow> _conditionOutcomes = new();
    private readonly ConcurrentDictionary<string, ParameterOverlay> _retrospectiveOverlays = new();

    // Debounce logging: log every Nth evaluation to avoid noise
    private int _evaluationCount;
    private const int LogSampleRate = 12; // ~every hour on 5m candles

    // Max retrospective confidence delta per condition (safety cap)
    private const int MaxRetrospectiveConfidenceDelta = 10;

    // Retrospective overlay expiry
    private static readonly TimeSpan RetrospectiveExpiry = TimeSpan.FromHours(48);

    // Track last condition for change detection logging
    private MarketConditionClass? _lastCondition;

    // Issue #5: force the very first live evaluation after startup to write an
    // adaptive_parameter_log row so there is no silence gap after restart.
    private bool _forceLogOnFirstEvaluation = true;

    // Runtime overrides (set via admin API, take precedence over DB config)
    private bool? _enabledOverride;
    private decimal? _intensityOverride;

    // Lock for serializing snapshot writes per condition key
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _snapshotLocks = new();

    public MarketAdaptiveParameterService(
        ILogger<MarketAdaptiveParameterService> logger,
        AdaptiveParameterLogRepository? logRepo = null,
        IAdaptiveStateRepository? stateRepo = null,
        IParameterRepository? paramRepo = null)
    {
        _logger = logger;
        _logRepo = logRepo;
        _stateRepo = stateRepo;
        _paramRepo = paramRepo;
    }

    /// <summary>Runtime override: enable/disable adaptive system (null = use DB config).</summary>
    public void SetEnabled(bool? enabled)
    {
        _enabledOverride = enabled;
        _logger.LogInformation("[Adaptive] Runtime enabled override set to {Override}", enabled?.ToString() ?? "cleared");
    }

    /// <summary>Runtime override: set overlay intensity (null = use DB config).</summary>
    public void SetIntensity(decimal? intensity)
    {
        _intensityOverride = intensity.HasValue ? Math.Clamp(intensity.Value, 0m, 1m) : null;
        _logger.LogInformation("[Adaptive] Runtime intensity override set to {Override}", _intensityOverride?.ToString("F2") ?? "cleared");
    }

    /// <summary>
    /// Issue #3: Reload persisted outcome windows + retrospective overlays from the DB.
    /// Call once at startup before the live tick processor begins evaluating.
    /// Safe to call without a state repository (no-op).
    /// </summary>
    public async Task LoadStateAsync(CancellationToken ct = default)
    {
        if (_stateRepo == null)
        {
            _logger.LogInformation("[Adaptive] No state repository configured; starting with empty state.");
            return;
        }

        try
        {
            var snapshots = await _stateRepo.LoadOutcomeWindowsAsync(ct);
            foreach (var snap in snapshots)
            {
                var window = new OutcomeWindow();
                foreach (var o in snap.Outcomes)
                    window.Add(o);
                _conditionOutcomes[snap.ConditionKey] = window;
            }

            var overlays = await _stateRepo.LoadRetrospectiveOverlaysAsync(ct);
            foreach (var entry in overlays)
                _retrospectiveOverlays[entry.ConditionKey] = entry.Overlay;

            _logger.LogInformation(
                "[Adaptive] State rehydrated: {Conditions} outcome windows, {Overlays} retrospective overlays",
                _conditionOutcomes.Count, _retrospectiveOverlays.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Adaptive] State rehydration failed (non-fatal); starting empty");
        }
    }

    /// <summary>
    /// Adapt base parameters for the current market conditions.
    /// Called before signal evaluation on each candle close.
    /// </summary>
    public (StrategyParameters AdaptedParams, MarketConditionClass Condition) AdaptParameters(
        StrategyParameters baseParams,
        IndicatorSnapshot snap,
        RegimeResult? regime,
        RichCandle candle,
        Timeframe tf,
        IReadOnlyList<IndicatorSnapshot> recentSnapshots)
    {
        var isEnabled = _enabledOverride ?? baseParams.AdaptiveParametersEnabled;
        if (!isEnabled)
            return (baseParams, MarketConditionClass.Default);

        // 1. Compute ATR SMA50 from snapshot buffer
        var atrSma50 = MarketConditionClassifier.ComputeAtrSma50(recentSnapshots);

        // 2. Classify market condition
        var condition = MarketConditionClassifier.Classify(snap, regime, candle, atrSma50, baseParams);

        // 3. Get retrospective overlay (if exists and not expired) — keyed by COARSE bucket
        ParameterOverlay? retroOverlay = null;
        if (baseParams.AdaptiveRetrospectiveEnabled)
        {
            var key = condition.ToCoarseKey();
            if (_retrospectiveOverlays.TryGetValue(key, out var overlay))
            {
                if (_conditionOutcomes.TryGetValue(key, out var window)
                    && (DateTimeOffset.UtcNow - window.LastUpdated) < RetrospectiveExpiry)
                {
                    retroOverlay = overlay;
                }
                else
                {
                    _retrospectiveOverlays.TryRemove(key, out _);
                    _ = _stateRepo?.DeleteRetrospectiveOverlayAsync(key, CancellationToken.None);
                    _logger.LogInformation(
                        "[Adaptive] Retrospective overlay expired for {Condition}", key);
                }
            }
        }

        // 4. Apply overlays with intensity scaling
        var intensity = _intensityOverride ?? baseParams.AdaptiveOverlayIntensity;
        var adapted = AdaptiveOverlayResolver.ApplyOverlays(
            baseParams, condition, intensity, retroOverlay);

        // 5. Log on condition change, post-restart force, or at sample rate
        _evaluationCount++;
        var conditionChanged = _lastCondition != null && _lastCondition != condition;
        var forceFirst = _forceLogOnFirstEvaluation;
        if (conditionChanged)
        {
            _logger.LogInformation(
                "[Adaptive] Condition changed: {From} → {To}",
                _lastCondition, condition);
        }
        _lastCondition = condition;

        if (_evaluationCount % LogSampleRate == 0 || conditionChanged || forceFirst)
        {
            LogAdaptation(baseParams, adapted, condition, retroOverlay != null);

            if (_logRepo != null)
            {
                var overlayDiffs = BuildOverlayDiffsJson(baseParams, adapted);
                var entry = new AdaptiveParameterLogEntry
                {
                    BarTimeUtc = candle.OpenTime,
                    ConditionClass = condition.ToKey(),
                    VolatilityTier = condition.Volatility.ToString(),
                    TrendStrength = condition.Trend.ToString(),
                    TradingSession = condition.Session.ToString(),
                    SpreadQuality = condition.Spread.ToString(),
                    VolumeTier = condition.Volume.ToString(),
                    BaseConfidenceBuy = baseParams.ConfidenceBuyThreshold,
                    AdaptedConfidenceBuy = adapted.ConfidenceBuyThreshold,
                    BaseConfidenceSell = baseParams.ConfidenceSellThreshold,
                    AdaptedConfidenceSell = adapted.ConfidenceSellThreshold,
                    OverlayDeltasJson = overlayDiffs,
                    Atr14 = snap.Atr14,
                    AtrSma50 = atrSma50,
                    Adx14 = snap.Adx14,
                    RegimeScore = regime?.RegimeScore ?? 0,
                    SpreadPct = snap.CloseMid > 0 ? snap.Spread / snap.CloseMid : 0m,
                };
                _ = _logRepo.LogAsync(entry, CancellationToken.None);
            }

            _forceLogOnFirstEvaluation = false;
        }

        // 6. Persist adapted parameters as a live snapshot on condition change
        if ((conditionChanged || forceFirst) && _paramRepo != null)
        {
            _ = PersistAdaptedSnapshotAsync(adapted, condition, CancellationToken.None);
        }

        return (adapted, condition);
    }

    /// <summary>
    /// Build the per-decision overlay diff JSON for the audit table. Public so call sites
    /// can stamp the same JSON onto SignalDecision records (Gap #2 / Issue #1).
    /// </summary>
    public static string? BuildOverlayDiffsJson(StrategyParameters baseParams, StrategyParameters adapted)
    {
        var diffs = new Dictionary<string, object>();
        if (adapted.ConfidenceBuyThreshold != baseParams.ConfidenceBuyThreshold)
            diffs["ConfBuyDelta"] = adapted.ConfidenceBuyThreshold - baseParams.ConfidenceBuyThreshold;
        if (adapted.ConfidenceSellThreshold != baseParams.ConfidenceSellThreshold)
            diffs["ConfSellDelta"] = adapted.ConfidenceSellThreshold - baseParams.ConfidenceSellThreshold;
        if (adapted.PullbackZonePct != baseParams.PullbackZonePct)
            diffs["PullbackZonePct"] = adapted.PullbackZonePct;
        if (adapted.MinAtrThreshold != baseParams.MinAtrThreshold)
            diffs["MinAtrThreshold"] = adapted.MinAtrThreshold;
        if (adapted.NeutralRegimePolicy != baseParams.NeutralRegimePolicy)
            diffs["NeutralRegimePolicy"] = adapted.NeutralRegimePolicy.ToString();
        if (adapted.VolumeMultiplierMin != baseParams.VolumeMultiplierMin)
            diffs["VolumeMultiplierMin"] = adapted.VolumeMultiplierMin;
        if (adapted.BodyRatioMin != baseParams.BodyRatioMin)
            diffs["BodyRatioMin"] = adapted.BodyRatioMin;
        if (adapted.MlMinWinProbability != baseParams.MlMinWinProbability)
            diffs["MlMinWinProbDelta"] = adapted.MlMinWinProbability - baseParams.MlMinWinProbability;
        if (adapted.ScalpCooldownBars != baseParams.ScalpCooldownBars)
            diffs["ScalpCooldownDelta"] = adapted.ScalpCooldownBars - baseParams.ScalpCooldownBars;
        return diffs.Count > 0 ? JsonSerializer.Serialize(diffs) : null;
    }

    /// <summary>
    /// Record a resolved outcome for retrospective refinement.
    /// Call after outcome resolution with the condition class active at signal generation.
    ///
    /// Issue #8: WIN, LOSS, and EXPIRED are all included so high-signal-rate conditions
    /// that produce expirations are properly penalized in expectancy.
    /// </summary>
    public void RecordOutcome(SignalOutcome outcome, string? conditionClassKey, StrategyParameters baseParams)
    {
        if (string.IsNullOrEmpty(conditionClassKey)) return;

        // Include WIN, LOSS, EXPIRED. AMBIGUOUS (multiple hits same bar) is intentionally
        // excluded because the realised PnL is undefined under our resolution rules.
        if (outcome.OutcomeLabel is not (OutcomeLabel.WIN or OutcomeLabel.LOSS or OutcomeLabel.EXPIRED))
            return;

        // Translate full key (or pre-coarsened key) to the coarse bucket used for
        // retrospective accumulation. Accept both formats so older signals still feed in.
        var key = ToCoarseKeyFromAny(conditionClassKey);

        var window = _conditionOutcomes.GetOrAdd(key, _ => new OutcomeWindow(baseParams.AdaptiveRetrospectiveWindowSize));
        window.Add(outcome);

        // Persist updated window snapshot (best-effort, fire-and-forget)
        _ = PersistOutcomeWindowAsync(key, window, CancellationToken.None);

        // Issue #4: honour the configured threshold instead of a hardcoded 15
        if (window.Count >= baseParams.AdaptiveRetrospectiveMinOutcomes)
            RecomputeRetrospective(key, window);
    }

    /// <summary>Current adaptive status for admin API.</summary>
    public AdaptiveStatus GetStatus(StrategyParameters baseParams)
    {
        return new AdaptiveStatus
        {
            Enabled = _enabledOverride ?? baseParams.AdaptiveParametersEnabled,
            EnabledOverride = _enabledOverride,
            Intensity = _intensityOverride ?? baseParams.AdaptiveOverlayIntensity,
            IntensityOverride = _intensityOverride,
            RetrospectiveEnabled = baseParams.AdaptiveRetrospectiveEnabled,
            CurrentCondition = _lastCondition?.ToKey(),
            TrackedConditionCount = _conditionOutcomes.Count,
            RetrospectiveOverlayCount = _retrospectiveOverlays.Count,
            ConditionDetails = _conditionOutcomes.Select(kvp => new ConditionDetail
            {
                ConditionKey = kvp.Key,
                OutcomeCount = kvp.Value.Count,
                WinRate = kvp.Value.WinRate,
                Expectancy = kvp.Value.Expectancy,
                HasRetrospectiveOverlay = _retrospectiveOverlays.ContainsKey(kvp.Key)
            }).ToList()
        };
    }

    private void RecomputeRetrospective(string conditionKey, OutcomeWindow window)
    {
        var expectancy = window.Expectancy;

        int confDelta;
        if (expectancy < 0.0m)
            confDelta = 5;
        else if (expectancy < 0.15m)
            confDelta = 3;
        else if (expectancy <= 0.40m)
        {
            // Healthy range — remove any retrospective overlay
            if (_retrospectiveOverlays.TryRemove(conditionKey, out _))
                _ = _stateRepo?.DeleteRetrospectiveOverlayAsync(conditionKey, CancellationToken.None);
            return;
        }
        else
            confDelta = -3;

        confDelta = Math.Clamp(confDelta, -MaxRetrospectiveConfidenceDelta, MaxRetrospectiveConfidenceDelta);

        var overlay = new ParameterOverlay
        {
            ConfidenceBuyThresholdDelta = confDelta,
            ConfidenceSellThresholdDelta = confDelta,
            OverlaySource = $"retrospective-{conditionKey}"
        };

        _retrospectiveOverlays[conditionKey] = overlay;
        _ = _stateRepo?.UpsertRetrospectiveOverlayAsync(conditionKey, overlay, CancellationToken.None);

        _logger.LogInformation(
            "[Adaptive] Retrospective update: {Condition} expectancy={Exp:F3}R → confDelta={Delta}",
            conditionKey, expectancy, confDelta);
    }

    private async Task PersistOutcomeWindowAsync(string key, OutcomeWindow window, CancellationToken ct)
    {
        if (_stateRepo == null) return;

        var sem = _snapshotLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            var snapshot = window.Snapshot();
            await _stateRepo.UpsertOutcomeWindowAsync(key, snapshot, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Adaptive] Outcome window persistence failed for {Key}", key);
        }
        finally
        {
            sem.Release();
        }
    }

    private async Task PersistAdaptedSnapshotAsync(
        StrategyParameters adapted, MarketConditionClass condition, CancellationToken ct)
    {
        try
        {
            var hash = ComputeHash(adapted.ToJson());
            var activeSet = await _paramRepo!.GetActiveAsync(adapted.StrategyVersion, ct);

            var set = new StrategyParameterSet
            {
                StrategyVersion = adapted.StrategyVersion,
                ParameterHash = hash,
                Parameters = adapted,
                Status = ParameterSetStatus.Active,
                CreatedUtc = DateTimeOffset.UtcNow,
                CreatedBy = "adaptive-live",
                Notes = $"Adaptive snapshot: {condition.ToKey()}"
            };

            var id = await _paramRepo.InsertAsync(set, ct);
            if (activeSet != null)
                await _paramRepo.ActivateAsync(id, activeSet.Id, "adaptive-live",
                    $"Market condition changed to {condition.ToKey()}", ct);

            _logger.LogInformation(
                "[Adaptive] Persisted live parameter snapshot id={Id} condition={Condition}",
                id, condition.ToKey());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Adaptive] Failed to persist adapted parameter snapshot");
        }
    }

    private static string ComputeHash(string json)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static string ToCoarseKeyFromAny(string conditionKey)
    {
        // Full key format: Volatility_Trend_Session_Spread_Volume
        // Coarse key format: Volatility_Trend
        // If the supplied key already has only two segments treat it as already coarse.
        var parts = conditionKey.Split('_');
        if (parts.Length >= 2)
            return $"{parts[0]}_{parts[1]}";
        return conditionKey;
    }

    private void LogAdaptation(
        StrategyParameters baseParams,
        StrategyParameters adapted,
        MarketConditionClass condition,
        bool hasRetro)
    {
        var parts = new List<string>();
        if (adapted.ConfidenceBuyThreshold != baseParams.ConfidenceBuyThreshold)
            parts.Add($"ConfBuy={baseParams.ConfidenceBuyThreshold}→{adapted.ConfidenceBuyThreshold}");
        if (adapted.ConfidenceSellThreshold != baseParams.ConfidenceSellThreshold)
            parts.Add($"ConfSell={baseParams.ConfidenceSellThreshold}→{adapted.ConfidenceSellThreshold}");
        if (adapted.PullbackZonePct != baseParams.PullbackZonePct)
            parts.Add($"Pullback={baseParams.PullbackZonePct}→{adapted.PullbackZonePct}");
        if (adapted.MinAtrThreshold != baseParams.MinAtrThreshold)
            parts.Add($"MinATR={baseParams.MinAtrThreshold}→{adapted.MinAtrThreshold:F2}");
        if (adapted.NeutralRegimePolicy != baseParams.NeutralRegimePolicy)
            parts.Add($"NeutralPolicy={baseParams.NeutralRegimePolicy}→{adapted.NeutralRegimePolicy}");

        if (parts.Count == 0) return;

        _logger.LogInformation(
            "[Adaptive] Condition={Condition} retro={HasRetro} {Changes}",
            condition, hasRetro, string.Join(" ", parts));
    }

    /// <summary>Rolling outcome window per condition class.</summary>
    internal sealed class OutcomeWindow
    {
        private readonly Queue<SignalOutcome> _outcomes = new();
        private readonly object _lock = new();
        private readonly int _maxSize;

        public OutcomeWindow(int maxSize = 30)
        {
            _maxSize = Math.Max(5, maxSize);
        }

        public DateTimeOffset LastUpdated { get; private set; }
        public int Count { get { lock (_lock) return _outcomes.Count; } }

        public void Add(SignalOutcome outcome)
        {
            lock (_lock)
            {
                _outcomes.Enqueue(outcome);
                while (_outcomes.Count > _maxSize)
                    _outcomes.Dequeue();
                if (outcome.EvaluatedAtUtc > LastUpdated)
                    LastUpdated = outcome.EvaluatedAtUtc;
            }
        }

        public IReadOnlyList<SignalOutcome> Snapshot()
        {
            lock (_lock) return _outcomes.ToList();
        }

        public decimal WinRate
        {
            get
            {
                lock (_lock)
                {
                    var wins = _outcomes.Count(o => o.OutcomeLabel == OutcomeLabel.WIN);
                    var resolved = _outcomes.Count(o =>
                        o.OutcomeLabel is OutcomeLabel.WIN or OutcomeLabel.LOSS or OutcomeLabel.EXPIRED);
                    return resolved > 0 ? (decimal)wins / resolved : 0m;
                }
            }
        }

        public decimal Expectancy
        {
            get
            {
                lock (_lock)
                {
                    // Issue #8: WIN, LOSS, EXPIRED all count toward expectancy.
                    // Use the actual realised PnlR mean across resolved outcomes — this
                    // generalises the original (winRate * avgWin - lossRate * avgLoss)
                    // formula and naturally absorbs EXPIRED outcomes whose PnlR may be
                    // small positive, small negative, or zero.
                    var resolved = _outcomes.Where(o =>
                        o.OutcomeLabel is OutcomeLabel.WIN or OutcomeLabel.LOSS or OutcomeLabel.EXPIRED).ToArray();
                    if (resolved.Length < 5) return 0m;

                    return resolved.Average(o => o.PnlR);
                }
            }
        }
    }
}

// ─── Admin DTOs ─────────────────────────────────────────

public sealed record AdaptiveStatus
{
    public bool Enabled { get; init; }
    public bool? EnabledOverride { get; init; }
    public decimal Intensity { get; init; }
    public decimal? IntensityOverride { get; init; }
    public bool RetrospectiveEnabled { get; init; }
    public string? CurrentCondition { get; init; }
    public int TrackedConditionCount { get; init; }
    public int RetrospectiveOverlayCount { get; init; }
    public List<ConditionDetail> ConditionDetails { get; init; } = [];
}

public sealed record ConditionDetail
{
    public required string ConditionKey { get; init; }
    public int OutcomeCount { get; init; }
    public decimal WinRate { get; init; }
    public decimal Expectancy { get; init; }
    public bool HasRetrospectiveOverlay { get; init; }
}
