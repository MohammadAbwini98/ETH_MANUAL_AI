using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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
///   2. Retrospective: refines overlays using rolling outcome windows per
///      timeframe + coarse condition bucket so each timeframe evolves independently.
///
/// Adapted parameters are per-evaluation (transient). Base parameters in the DB
/// are never mutated. Outcome windows, retrospective overlays, and the latest
/// active per-timeframe adaptive profile state are persisted to DB so they survive
/// process restarts and can be surfaced on the dashboard.
/// </summary>
public sealed class MarketAdaptiveParameterService
{
    private const int LogSampleRate = 12;
    private const int MaxRetrospectiveConfidenceDelta = 10;
    private const int MaxRecentProfileChanges = 60;
    private static readonly TimeSpan RetrospectiveExpiry = TimeSpan.FromHours(48);

    private readonly ILogger<MarketAdaptiveParameterService> _logger;
    private readonly AdaptiveParameterLogRepository? _logRepo;
    private readonly IAdaptiveStateRepository? _stateRepo;

    private readonly ConcurrentDictionary<string, OutcomeWindow> _conditionOutcomes = new();
    private readonly ConcurrentDictionary<string, ParameterOverlay> _retrospectiveOverlays = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _snapshotLocks = new();
    private readonly ConcurrentDictionary<string, TimeframeAdaptiveRuntimeState> _timeframeRuntimeStates = new();
    private readonly ConcurrentQueue<AdaptiveTimeframeProfileChange> _recentProfileChanges = new();

    private bool? _enabledOverride;
    private decimal? _intensityOverride;

    public MarketAdaptiveParameterService(
        ILogger<MarketAdaptiveParameterService> logger,
        AdaptiveParameterLogRepository? logRepo = null,
        IAdaptiveStateRepository? stateRepo = null,
        IParameterRepository? paramRepo = null)
    {
        _logger = logger;
        _logRepo = logRepo;
        _stateRepo = stateRepo;
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
    /// Reload persisted adaptive state so retrospective overlays and latest
    /// per-timeframe adaptive setups survive restart.
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

            var profileStates = await _stateRepo.LoadTimeframeProfileStatesAsync(ct);
            foreach (var state in profileStates)
            {
                var runtime = _timeframeRuntimeStates.GetOrAdd(state.Timeframe, _ => new TimeframeAdaptiveRuntimeState());
                lock (runtime.Gate)
                {
                    runtime.CurrentProfile = state;
                    runtime.LastConditionKey = state.CurrentConditionClass;
                }
            }

            var recentChanges = await _stateRepo.LoadRecentTimeframeProfileChangesAsync(MaxRecentProfileChanges, ct);
            foreach (var change in recentChanges.OrderBy(c => c.ChangedAtUtc))
                EnqueueRecentChange(change);

            _logger.LogInformation(
                "[Adaptive] State rehydrated: {Conditions} outcome windows, {Overlays} retrospective overlays, {Profiles} timeframe profiles",
                _conditionOutcomes.Count, _retrospectiveOverlays.Count, profileStates.Count);
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
        var resolvedBaseParams = baseParams.ResolveForTimeframe(tf.Name);
        var isEnabled = _enabledOverride ?? resolvedBaseParams.AdaptiveParametersEnabled;
        if (!isEnabled)
            return (resolvedBaseParams, MarketConditionClass.Default);

        var atrSma50 = MarketConditionClassifier.ComputeAtrSma50(recentSnapshots);
        var condition = MarketConditionClassifier.Classify(snap, regime, candle, atrSma50, resolvedBaseParams);
        var conditionKey = condition.ToKey();
        var scopedCoarseKey = ToScopedConditionKey(tf.Name, conditionKey);

        ParameterOverlay? retroOverlay = null;
        if (resolvedBaseParams.AdaptiveRetrospectiveEnabled)
        {
            if (_retrospectiveOverlays.TryGetValue(scopedCoarseKey, out var overlay))
            {
                if (_conditionOutcomes.TryGetValue(scopedCoarseKey, out var window)
                    && (DateTimeOffset.UtcNow - window.LastUpdated) < RetrospectiveExpiry)
                {
                    retroOverlay = overlay;
                }
                else
                {
                    _retrospectiveOverlays.TryRemove(scopedCoarseKey, out _);
                    _ = _stateRepo?.DeleteRetrospectiveOverlayAsync(scopedCoarseKey, CancellationToken.None);
                    _logger.LogInformation("[Adaptive:{Timeframe}] Retrospective overlay expired for {Condition}",
                        tf.Name, scopedCoarseKey);
                }
            }
        }

        var intensity = _intensityOverride ?? resolvedBaseParams.AdaptiveOverlayIntensity;
        var adapted = AdaptiveOverlayResolver.ApplyOverlays(
            resolvedBaseParams, condition, intensity, retroOverlay);

        var runtime = _timeframeRuntimeStates.GetOrAdd(tf.Name, _ => new TimeframeAdaptiveRuntimeState());
        AdaptiveTimeframeProfileState? newProfileState = null;
        AdaptiveTimeframeProfileChange? profileChange = null;
        bool shouldLog;
        bool conditionChanged;
        bool forceFirst;

        lock (runtime.Gate)
        {
            runtime.EvaluationCount++;
            conditionChanged = runtime.LastConditionKey != null
                && !string.Equals(runtime.LastConditionKey, conditionKey, StringComparison.Ordinal);
            forceFirst = runtime.ForceLogOnFirstEvaluation;
            shouldLog = runtime.EvaluationCount % LogSampleRate == 0 || conditionChanged || forceFirst;

            if (conditionChanged)
            {
                _logger.LogInformation("[Adaptive:{Timeframe}] Condition changed: {From} → {To}",
                    tf.Name, runtime.LastConditionKey, conditionKey);
            }

            var baseHash = ComputeHash(resolvedBaseParams.ToJson());
            var effectiveHash = ComputeHash(adapted.ToJson());
            var overlayDiffs = BuildOverlayDiffsJson(resolvedBaseParams, adapted);
            var currentProfile = BuildProfileState(
                snap.Symbol,
                tf.Name,
                resolvedBaseParams,
                adapted,
                condition,
                retroOverlay,
                intensity,
                overlayDiffs,
                baseHash,
                effectiveHash,
                candle.OpenTime,
                previous: runtime.CurrentProfile);

            if (HasProfileChanged(runtime.CurrentProfile, currentProfile))
            {
                newProfileState = currentProfile;
                profileChange = BuildProfileChange(runtime.CurrentProfile, currentProfile, candle.OpenTime);
                runtime.CurrentProfile = currentProfile;
            }

            runtime.LastConditionKey = conditionKey;
            if (shouldLog)
                runtime.ForceLogOnFirstEvaluation = false;
        }

        if (shouldLog)
        {
            LogAdaptation(tf.Name, resolvedBaseParams, adapted, condition, retroOverlay != null);

            if (_logRepo != null)
            {
                var entry = new AdaptiveParameterLogEntry
                {
                    BarTimeUtc = candle.OpenTime,
                    ConditionClass = conditionKey,
                    VolatilityTier = condition.Volatility.ToString(),
                    TrendStrength = condition.Trend.ToString(),
                    TradingSession = condition.Session.ToString(),
                    SpreadQuality = condition.Spread.ToString(),
                    VolumeTier = condition.Volume.ToString(),
                    BaseConfidenceBuy = resolvedBaseParams.ConfidenceBuyThreshold,
                    AdaptedConfidenceBuy = adapted.ConfidenceBuyThreshold,
                    BaseConfidenceSell = resolvedBaseParams.ConfidenceSellThreshold,
                    AdaptedConfidenceSell = adapted.ConfidenceSellThreshold,
                    OverlayDeltasJson = BuildOverlayDiffsJson(resolvedBaseParams, adapted),
                    Atr14 = snap.Atr14,
                    AtrSma50 = atrSma50,
                    Adx14 = snap.Adx14,
                    RegimeScore = regime?.RegimeScore ?? 0,
                    SpreadPct = snap.CloseMid > 0 ? snap.Spread / snap.CloseMid : 0m,
                };
                _ = _logRepo.LogAsync(entry, CancellationToken.None);
            }
        }

        if (profileChange != null)
            EnqueueRecentChange(profileChange);

        if (newProfileState != null)
            _ = PersistProfileStateAsync(newProfileState, profileChange, CancellationToken.None);

        return (adapted, condition);
    }

    /// <summary>
    /// Build the per-decision overlay diff JSON for the audit table. Public so call sites
    /// can stamp the same JSON onto SignalDecision records.
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

    public void RecordOutcome(SignalOutcome outcome, string? conditionClassKey, StrategyParameters baseParams)
        => RecordOutcome(outcome, baseParams.TimeframePrimary, conditionClassKey, baseParams);

    /// <summary>
    /// Record a resolved outcome for retrospective refinement. Timeframe is part of the
    /// state key so each timeframe evolves independently.
    /// </summary>
    public void RecordOutcome(
        SignalOutcome outcome,
        string timeframe,
        string? conditionClassKey,
        StrategyParameters baseParams)
    {
        if (string.IsNullOrEmpty(conditionClassKey))
            return;

        if (outcome.OutcomeLabel is not (OutcomeLabel.WIN or OutcomeLabel.LOSS or OutcomeLabel.EXPIRED))
            return;

        var resolvedParams = baseParams.ResolveForTimeframe(timeframe);
        var key = ToScopedConditionKey(timeframe, conditionClassKey);
        var window = _conditionOutcomes.GetOrAdd(key, _ => new OutcomeWindow(resolvedParams.AdaptiveRetrospectiveWindowSize));
        window.Add(outcome);

        _ = PersistOutcomeWindowAsync(key, window, CancellationToken.None);

        if (window.Count >= resolvedParams.AdaptiveRetrospectiveMinOutcomes)
            RecomputeRetrospective(timeframe, key, window);
    }

    /// <summary>Current adaptive status for admin API / dashboard.</summary>
    public AdaptiveStatus GetStatus(StrategyParameters baseParams)
    {
        var profiles = _timeframeRuntimeStates.Values
            .Select(state =>
            {
                lock (state.Gate)
                {
                    return state.CurrentProfile;
                }
            })
            .Where(state => state != null)
            .Select(state => ToView(state!))
            .OrderBy(state => Timeframe.ByNameOrDefault(state.Timeframe).Minutes)
            .ToList();

        var primaryCurrent = profiles.FirstOrDefault(p => string.Equals(p.Timeframe, baseParams.TimeframePrimary, StringComparison.OrdinalIgnoreCase));

        return new AdaptiveStatus
        {
            Enabled = _enabledOverride ?? baseParams.AdaptiveParametersEnabled,
            EnabledOverride = _enabledOverride,
            Intensity = _intensityOverride ?? baseParams.AdaptiveOverlayIntensity,
            IntensityOverride = _intensityOverride,
            RetrospectiveEnabled = baseParams.AdaptiveRetrospectiveEnabled,
            CurrentCondition = primaryCurrent?.CurrentConditionClass,
            TrackedConditionCount = _conditionOutcomes.Count,
            RetrospectiveOverlayCount = _retrospectiveOverlays.Count,
            ConditionDetails = _conditionOutcomes
                .Select(kvp =>
                {
                    var parsed = ParseScopedConditionKey(kvp.Key);
                    return new ConditionDetail
                    {
                        Timeframe = parsed.Timeframe,
                        ConditionKey = parsed.ConditionKey,
                        OutcomeCount = kvp.Value.Count,
                        WinRate = kvp.Value.WinRate,
                        Expectancy = kvp.Value.Expectancy,
                        HasRetrospectiveOverlay = _retrospectiveOverlays.ContainsKey(kvp.Key)
                    };
                })
                .OrderBy(detail => Timeframe.ByNameOrDefault(detail.Timeframe).Minutes)
                .ThenByDescending(detail => detail.OutcomeCount)
                .ToList(),
            TimeframeProfiles = profiles,
            RecentChanges = _recentProfileChanges
                .OrderByDescending(change => change.ChangedAtUtc)
                .Take(20)
                .Select(ToView)
                .ToList()
        };
    }

    private void RecomputeRetrospective(string timeframe, string conditionKey, OutcomeWindow window)
    {
        var expectancy = window.Expectancy;

        int confDelta;
        if (expectancy < 0.0m)
            confDelta = 5;
        else if (expectancy < 0.15m)
            confDelta = 3;
        else if (expectancy <= 0.40m)
        {
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
            "[Adaptive:{Timeframe}] Retrospective update: {Condition} expectancy={Exp:F3}R → confDelta={Delta}",
            timeframe, conditionKey, expectancy, confDelta);
    }

    private async Task PersistOutcomeWindowAsync(string key, OutcomeWindow window, CancellationToken ct)
    {
        if (_stateRepo == null) return;

        var sem = _snapshotLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            await _stateRepo.UpsertOutcomeWindowAsync(key, window.Snapshot(), ct);
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

    private async Task PersistProfileStateAsync(
        AdaptiveTimeframeProfileState state,
        AdaptiveTimeframeProfileChange? change,
        CancellationToken ct)
    {
        if (_stateRepo == null) return;

        try
        {
            await _stateRepo.UpsertTimeframeProfileStateAsync(state, ct);
            if (change != null)
                await _stateRepo.AppendTimeframeProfileChangeAsync(change, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Adaptive] Timeframe profile persistence failed for {Symbol}/{Timeframe}",
                state.Symbol, state.Timeframe);
        }
    }

    private static string ToScopedConditionKey(string timeframe, string conditionKey)
        => $"{timeframe.Trim().ToLowerInvariant()}|{ToCoarseKeyFromAny(conditionKey)}";

    private static (string Timeframe, string ConditionKey) ParseScopedConditionKey(string scopedKey)
    {
        var parts = scopedKey.Split('|', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2
            ? (parts[0], parts[1])
            : ("legacy", ToCoarseKeyFromAny(scopedKey));
    }

    private static string ToCoarseKeyFromAny(string conditionKey)
    {
        var parts = conditionKey.Split('_');
        if (parts.Length >= 2)
            return $"{parts[0]}_{parts[1]}";
        return conditionKey;
    }

    private static string ComputeHash(string json)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static bool HasProfileChanged(AdaptiveTimeframeProfileState? previous, AdaptiveTimeframeProfileState current)
    {
        if (previous == null)
            return true;

        return !string.Equals(previous.CurrentConditionClass, current.CurrentConditionClass, StringComparison.Ordinal)
               || !string.Equals(previous.CurrentCoarseConditionKey, current.CurrentCoarseConditionKey, StringComparison.Ordinal)
               || !string.Equals(previous.BaseParameterHash, current.BaseParameterHash, StringComparison.Ordinal)
               || !string.Equals(previous.EffectiveParameterHash, current.EffectiveParameterHash, StringComparison.Ordinal)
               || previous.HasRetrospectiveOverlay != current.HasRetrospectiveOverlay
               || previous.AdaptiveEnabled != current.AdaptiveEnabled
               || previous.RetrospectiveEnabled != current.RetrospectiveEnabled
               || previous.EffectiveIntensity != current.EffectiveIntensity
               || previous.ProfileBucket != current.ProfileBucket
               || !string.Equals(previous.StrategyVersion, current.StrategyVersion, StringComparison.Ordinal);
    }

    private static AdaptiveTimeframeProfileState BuildProfileState(
        string symbol,
        string timeframe,
        StrategyParameters baseParameters,
        StrategyParameters effectiveParameters,
        MarketConditionClass condition,
        ParameterOverlay? retrospectiveOverlay,
        decimal intensity,
        string? overlayDiffsJson,
        string baseHash,
        string effectiveHash,
        DateTimeOffset barTimeUtc,
        AdaptiveTimeframeProfileState? previous)
    {
        return new AdaptiveTimeframeProfileState
        {
            Symbol = symbol,
            Timeframe = timeframe,
            StrategyVersion = effectiveParameters.StrategyVersion,
            ProfileBucket = effectiveParameters.ResolveTimeframeProfileBucket(timeframe),
            AdaptiveEnabled = effectiveParameters.AdaptiveParametersEnabled,
            RetrospectiveEnabled = effectiveParameters.AdaptiveRetrospectiveEnabled,
            HasRetrospectiveOverlay = retrospectiveOverlay != null,
            EffectiveIntensity = intensity,
            CurrentConditionClass = condition.ToKey(),
            CurrentCoarseConditionKey = condition.ToCoarseKey(),
            OverlayDiffsJson = overlayDiffsJson,
            RetrospectiveOverlay = retrospectiveOverlay,
            BaseParameters = baseParameters,
            EffectiveParameters = effectiveParameters,
            BaseParameterHash = baseHash,
            EffectiveParameterHash = effectiveHash,
            LastEvaluatedBarUtc = barTimeUtc,
            LastChangedUtc = DateTimeOffset.UtcNow,
            ChangeVersion = previous?.ChangeVersion + 1 ?? 1
        };
    }

    private static AdaptiveTimeframeProfileChange BuildProfileChange(
        AdaptiveTimeframeProfileState? previous,
        AdaptiveTimeframeProfileState current,
        DateTimeOffset barTimeUtc)
    {
        var reasons = new List<string>();
        if (previous == null)
        {
            reasons.Add("initialised");
        }
        else
        {
            if (!string.Equals(previous.CurrentConditionClass, current.CurrentConditionClass, StringComparison.Ordinal))
                reasons.Add("condition");
            if (!string.Equals(previous.BaseParameterHash, current.BaseParameterHash, StringComparison.Ordinal))
                reasons.Add("base-parameters");
            if (!string.Equals(previous.EffectiveParameterHash, current.EffectiveParameterHash, StringComparison.Ordinal))
                reasons.Add("effective-parameters");
            if (previous.HasRetrospectiveOverlay != current.HasRetrospectiveOverlay)
                reasons.Add("retrospective-overlay");
            if (previous.EffectiveIntensity != current.EffectiveIntensity)
                reasons.Add("intensity");
        }

        return new AdaptiveTimeframeProfileChange
        {
            Symbol = current.Symbol,
            Timeframe = current.Timeframe,
            StrategyVersion = current.StrategyVersion,
            ProfileBucket = current.ProfileBucket,
            ChangeReason = reasons.Count > 0 ? string.Join(", ", reasons) : "refresh",
            PreviousConditionClass = previous?.CurrentConditionClass,
            CurrentConditionClass = current.CurrentConditionClass,
            PreviousParameterHash = previous?.EffectiveParameterHash,
            CurrentParameterHash = current.EffectiveParameterHash,
            AdaptiveEnabled = current.AdaptiveEnabled,
            RetrospectiveEnabled = current.RetrospectiveEnabled,
            HasRetrospectiveOverlay = current.HasRetrospectiveOverlay,
            EffectiveIntensity = current.EffectiveIntensity,
            OverlayDiffsJson = current.OverlayDiffsJson,
            RetrospectiveOverlay = current.RetrospectiveOverlay,
            BaseParameters = current.BaseParameters,
            EffectiveParameters = current.EffectiveParameters,
            BarTimeUtc = barTimeUtc,
            ChangedAtUtc = current.LastChangedUtc,
            ChangeVersion = current.ChangeVersion
        };
    }

    private void EnqueueRecentChange(AdaptiveTimeframeProfileChange change)
    {
        _recentProfileChanges.Enqueue(change);
        while (_recentProfileChanges.Count > MaxRecentProfileChanges && _recentProfileChanges.TryDequeue(out _))
        {
        }
    }

    private void LogAdaptation(
        string timeframe,
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
            "[Adaptive:{Timeframe}] Condition={Condition} retro={HasRetro} {Changes}",
            timeframe, condition, hasRetro, string.Join(" ", parts));
    }

    private static AdaptiveTimeframeProfileView ToView(AdaptiveTimeframeProfileState state)
    {
        var p = state.EffectiveParameters;
        return new AdaptiveTimeframeProfileView
        {
            Timeframe = state.Timeframe,
            StrategyVersion = state.StrategyVersion,
            ProfileBucket = state.ProfileBucket.ToString(),
            AdaptiveEnabled = state.AdaptiveEnabled,
            RetrospectiveEnabled = state.RetrospectiveEnabled,
            HasRetrospectiveOverlay = state.HasRetrospectiveOverlay,
            EffectiveIntensity = state.EffectiveIntensity,
            CurrentConditionClass = state.CurrentConditionClass,
            EffectiveParameterHash = state.EffectiveParameterHash,
            ConfidenceBuyThreshold = p.ConfidenceBuyThreshold,
            ConfidenceSellThreshold = p.ConfidenceSellThreshold,
            PullbackZonePct = p.PullbackZonePct,
            MinAtrThreshold = p.MinAtrThreshold,
            StopAtrMultiplier = p.StopAtrMultiplier,
            TargetRMultiple = p.TargetRMultiple,
            MlMinWinProbability = p.MlMinWinProbability,
            NeutralRegimePolicy = p.NeutralRegimePolicy.ToString(),
            LastChangedUtc = state.LastChangedUtc
        };
    }

    private static AdaptiveTimeframeProfileChangeView ToView(AdaptiveTimeframeProfileChange change)
    {
        var p = change.EffectiveParameters;
        return new AdaptiveTimeframeProfileChangeView
        {
            Timeframe = change.Timeframe,
            StrategyVersion = change.StrategyVersion,
            ProfileBucket = change.ProfileBucket.ToString(),
            ChangeReason = change.ChangeReason,
            PreviousConditionClass = change.PreviousConditionClass,
            CurrentConditionClass = change.CurrentConditionClass,
            CurrentParameterHash = change.CurrentParameterHash,
            HasRetrospectiveOverlay = change.HasRetrospectiveOverlay,
            EffectiveIntensity = change.EffectiveIntensity,
            ConfidenceBuyThreshold = p.ConfidenceBuyThreshold,
            ConfidenceSellThreshold = p.ConfidenceSellThreshold,
            StopAtrMultiplier = p.StopAtrMultiplier,
            TargetRMultiple = p.TargetRMultiple,
            ChangedAtUtc = change.ChangedAtUtc
        };
    }

    private sealed class TimeframeAdaptiveRuntimeState
    {
        public object Gate { get; } = new();
        public int EvaluationCount { get; set; }
        public bool ForceLogOnFirstEvaluation { get; set; } = true;
        public string? LastConditionKey { get; set; }
        public AdaptiveTimeframeProfileState? CurrentProfile { get; set; }
    }

    /// <summary>Rolling outcome window per timeframe/condition bucket.</summary>
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
    public List<AdaptiveTimeframeProfileView> TimeframeProfiles { get; init; } = [];
    public List<AdaptiveTimeframeProfileChangeView> RecentChanges { get; init; } = [];
}

public sealed record ConditionDetail
{
    public required string Timeframe { get; init; }
    public required string ConditionKey { get; init; }
    public int OutcomeCount { get; init; }
    public decimal WinRate { get; init; }
    public decimal Expectancy { get; init; }
    public bool HasRetrospectiveOverlay { get; init; }
}

public sealed record AdaptiveTimeframeProfileView
{
    public required string Timeframe { get; init; }
    public required string StrategyVersion { get; init; }
    public required string ProfileBucket { get; init; }
    public bool AdaptiveEnabled { get; init; }
    public bool RetrospectiveEnabled { get; init; }
    public bool HasRetrospectiveOverlay { get; init; }
    public decimal EffectiveIntensity { get; init; }
    public string? CurrentConditionClass { get; init; }
    public required string EffectiveParameterHash { get; init; }
    public int ConfidenceBuyThreshold { get; init; }
    public int ConfidenceSellThreshold { get; init; }
    public decimal PullbackZonePct { get; init; }
    public decimal MinAtrThreshold { get; init; }
    public decimal StopAtrMultiplier { get; init; }
    public decimal TargetRMultiple { get; init; }
    public decimal MlMinWinProbability { get; init; }
    public required string NeutralRegimePolicy { get; init; }
    public DateTimeOffset LastChangedUtc { get; init; }
}

public sealed record AdaptiveTimeframeProfileChangeView
{
    public required string Timeframe { get; init; }
    public required string StrategyVersion { get; init; }
    public required string ProfileBucket { get; init; }
    public required string ChangeReason { get; init; }
    public string? PreviousConditionClass { get; init; }
    public string? CurrentConditionClass { get; init; }
    public required string CurrentParameterHash { get; init; }
    public bool HasRetrospectiveOverlay { get; init; }
    public decimal EffectiveIntensity { get; init; }
    public int ConfidenceBuyThreshold { get; init; }
    public int ConfidenceSellThreshold { get; init; }
    public decimal StopAtrMultiplier { get; init; }
    public decimal TargetRMultiple { get; init; }
    public DateTimeOffset ChangedAtUtc { get; init; }
}
