using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Engine.ML;

/// <summary>
/// Resolves market condition classes to parameter overlays and applies them.
/// Merging rules:
///   - Delta fields: summed across all dimension overlays
///   - Override fields: most restrictive wins
///   - Enum overrides: most restrictive wins (BlockAll > AllowReducedRisk > AllowMlGated)
///   - Final result: clamped to ParameterBands
/// </summary>
public static class AdaptiveOverlayResolver
{
    /// <summary>
    /// Apply proactive + optional retrospective overlay to base parameters.
    /// Returns clamped, validated adapted parameters.
    /// </summary>
    public static StrategyParameters ApplyOverlays(
        StrategyParameters baseParams,
        MarketConditionClass condition,
        decimal intensity,
        ParameterOverlay? retrospective = null)
    {
        var overlays = GetProactiveOverlays(condition);
        if (retrospective != null)
            overlays.Add(retrospective);

        var merged = MergeOverlays(overlays);
        var adapted = ApplyOverlay(baseParams, merged, intensity);
        var clamped = ParameterBands.Clamp(adapted, baseParams);

        // Final validation — if invalid, fall back to base
        var error = clamped.Validate();
        return error != null ? baseParams : clamped;
    }

    /// <summary>Get all proactive overlays for each dimension of the condition.</summary>
    internal static List<ParameterOverlay> GetProactiveOverlays(MarketConditionClass condition)
    {
        var overlays = new List<ParameterOverlay>(5);

        var vol = GetVolatilityOverlay(condition.Volatility);
        if (vol != null) overlays.Add(vol);

        var trend = GetTrendOverlay(condition.Trend);
        if (trend != null) overlays.Add(trend);

        var session = GetSessionOverlay(condition.Session);
        if (session != null) overlays.Add(session);

        var spread = GetSpreadOverlay(condition.Spread);
        if (spread != null) overlays.Add(spread);

        var volume = GetVolumeOverlay(condition.Volume);
        if (volume != null) overlays.Add(volume);

        return overlays;
    }

    /// <summary>Merge multiple overlays into a single composite overlay.</summary>
    internal static ParameterOverlay MergeOverlays(IReadOnlyList<ParameterOverlay> overlays)
    {
        if (overlays.Count == 0) return new ParameterOverlay();
        if (overlays.Count == 1) return overlays[0];

        int? confBuyDelta = null, confSellDelta = null, conflictGapDelta = null;
        int? regimeAgeDelta = null, scalpCooldownDelta = null, scalpConfDelta = null;
        decimal? mlWinProbDelta = null, mlAccFirstDelta = null, mlWeakBumpDelta = null;
        decimal? pullbackOverride = null, atrMultOverride = null;
        decimal? volumeMultOverride = null, bodyRatioOverride = null, scalpAtrMultOverride = null;
        NeutralRegimePolicy? neutralPolicy = null;

        foreach (var o in overlays)
        {
            // Sum deltas
            if (o.ConfidenceBuyThresholdDelta.HasValue)
                confBuyDelta = (confBuyDelta ?? 0) + o.ConfidenceBuyThresholdDelta.Value;
            if (o.ConfidenceSellThresholdDelta.HasValue)
                confSellDelta = (confSellDelta ?? 0) + o.ConfidenceSellThresholdDelta.Value;
            if (o.ConflictingScoreGapDelta.HasValue)
                conflictGapDelta = (conflictGapDelta ?? 0) + o.ConflictingScoreGapDelta.Value;
            if (o.MaxRecoveredRegimeAgeBarsDelta.HasValue)
                regimeAgeDelta = (regimeAgeDelta ?? 0) + o.MaxRecoveredRegimeAgeBarsDelta.Value;
            if (o.ScalpCooldownBarsDelta.HasValue)
                scalpCooldownDelta = (scalpCooldownDelta ?? 0) + o.ScalpCooldownBarsDelta.Value;
            if (o.ScalpConfidenceThresholdDelta.HasValue)
                scalpConfDelta = (scalpConfDelta ?? 0) + o.ScalpConfidenceThresholdDelta.Value;
            if (o.MlMinWinProbabilityDelta.HasValue)
                mlWinProbDelta = (mlWinProbDelta ?? 0) + o.MlMinWinProbabilityDelta.Value;
            if (o.MlAccuracyFirstMinWinProbDelta.HasValue)
                mlAccFirstDelta = (mlAccFirstDelta ?? 0) + o.MlAccuracyFirstMinWinProbDelta.Value;
            if (o.MlWeakContextBumpDelta.HasValue)
                mlWeakBumpDelta = (mlWeakBumpDelta ?? 0) + o.MlWeakContextBumpDelta.Value;

            // Overrides: most restrictive wins
            // PullbackZone: smaller is more restrictive
            if (o.PullbackZonePctOverride.HasValue)
                pullbackOverride = pullbackOverride.HasValue
                    ? Math.Min(pullbackOverride.Value, o.PullbackZonePctOverride.Value)
                    : o.PullbackZonePctOverride.Value;

            // ATR multiplier: higher is more restrictive (requires bigger moves)
            if (o.MinAtrThresholdMultiplier.HasValue)
                atrMultOverride = atrMultOverride.HasValue
                    ? Math.Max(atrMultOverride.Value, o.MinAtrThresholdMultiplier.Value)
                    : o.MinAtrThresholdMultiplier.Value;

            // Volume multiplier: keeping as-is — use the first non-null (lower relaxes volume gate when expected)
            if (o.VolumeMultiplierMinOverride.HasValue)
                volumeMultOverride ??= o.VolumeMultiplierMinOverride.Value;

            // Body ratio: higher is more restrictive
            if (o.BodyRatioMinOverride.HasValue)
                bodyRatioOverride = bodyRatioOverride.HasValue
                    ? Math.Max(bodyRatioOverride.Value, o.BodyRatioMinOverride.Value)
                    : o.BodyRatioMinOverride.Value;

            // Scalp ATR multiplier
            if (o.ScalpMinAtrMultiplier.HasValue)
                scalpAtrMultOverride = scalpAtrMultOverride.HasValue
                    ? Math.Max(scalpAtrMultOverride.Value, o.ScalpMinAtrMultiplier.Value)
                    : o.ScalpMinAtrMultiplier.Value;

            // Neutral regime policy: most restrictive wins (lower ordinal = more restrictive)
            if (o.NeutralRegimePolicyOverride.HasValue)
                neutralPolicy = neutralPolicy.HasValue
                    ? (NeutralRegimePolicy)Math.Min((int)neutralPolicy.Value, (int)o.NeutralRegimePolicyOverride.Value)
                    : o.NeutralRegimePolicyOverride.Value;
        }

        return new ParameterOverlay
        {
            ConfidenceBuyThresholdDelta = confBuyDelta,
            ConfidenceSellThresholdDelta = confSellDelta,
            ConflictingScoreGapDelta = conflictGapDelta,
            MaxRecoveredRegimeAgeBarsDelta = regimeAgeDelta,
            ScalpCooldownBarsDelta = scalpCooldownDelta,
            ScalpConfidenceThresholdDelta = scalpConfDelta,
            MlMinWinProbabilityDelta = mlWinProbDelta,
            MlAccuracyFirstMinWinProbDelta = mlAccFirstDelta,
            MlWeakContextBumpDelta = mlWeakBumpDelta,
            PullbackZonePctOverride = pullbackOverride,
            MinAtrThresholdMultiplier = atrMultOverride,
            VolumeMultiplierMinOverride = volumeMultOverride,
            BodyRatioMinOverride = bodyRatioOverride,
            ScalpMinAtrMultiplier = scalpAtrMultOverride,
            NeutralRegimePolicyOverride = neutralPolicy,
            OverlaySource = "merged"
        };
    }

    /// <summary>Apply a merged overlay to base parameters, scaled by intensity (0.0–1.0).</summary>
    internal static StrategyParameters ApplyOverlay(StrategyParameters p, ParameterOverlay o, decimal intensity)
    {
        intensity = Math.Clamp(intensity, 0m, 1m);
        if (intensity == 0m) return p;

        int ScaleDelta(int? delta) => delta.HasValue ? (int)Math.Round(delta.Value * intensity) : 0;
        decimal ScaleDecimalDelta(decimal? delta) => delta.HasValue ? delta.Value * intensity : 0m;

        var adapted = p with
        {
            ConfidenceBuyThreshold = p.ConfidenceBuyThreshold + ScaleDelta(o.ConfidenceBuyThresholdDelta),
            ConfidenceSellThreshold = p.ConfidenceSellThreshold + ScaleDelta(o.ConfidenceSellThresholdDelta),
            ConflictingScoreGap = p.ConflictingScoreGap + ScaleDelta(o.ConflictingScoreGapDelta),
            MaxRecoveredRegimeAgeBars = p.MaxRecoveredRegimeAgeBars + ScaleDelta(o.MaxRecoveredRegimeAgeBarsDelta),
            ScalpCooldownBars = p.ScalpCooldownBars + ScaleDelta(o.ScalpCooldownBarsDelta),
            ScalpConfidenceThreshold = p.ScalpConfidenceThreshold + ScaleDelta(o.ScalpConfidenceThresholdDelta),
            MlMinWinProbability = p.MlMinWinProbability + ScaleDecimalDelta(o.MlMinWinProbabilityDelta),
            MlAccuracyFirstMinWinProbability = p.MlAccuracyFirstMinWinProbability + ScaleDecimalDelta(o.MlAccuracyFirstMinWinProbDelta),
            MlWeakContextMinWinProbabilityBump = p.MlWeakContextMinWinProbabilityBump + ScaleDecimalDelta(o.MlWeakContextBumpDelta),
        };

        // Override fields: apply directly when intensity > 0 (interpolate between base and override)
        if (o.PullbackZonePctOverride.HasValue)
            adapted = adapted with { PullbackZonePct = Lerp(p.PullbackZonePct, o.PullbackZonePctOverride.Value, intensity) };

        if (o.MinAtrThresholdMultiplier.HasValue)
        {
            var targetAtr = p.MinAtrThreshold * o.MinAtrThresholdMultiplier.Value;
            adapted = adapted with { MinAtrThreshold = Lerp(p.MinAtrThreshold, targetAtr, intensity) };
        }

        if (o.VolumeMultiplierMinOverride.HasValue)
            adapted = adapted with { VolumeMultiplierMin = Lerp(p.VolumeMultiplierMin, o.VolumeMultiplierMinOverride.Value, intensity) };

        if (o.BodyRatioMinOverride.HasValue)
            adapted = adapted with { BodyRatioMin = Lerp(p.BodyRatioMin, o.BodyRatioMinOverride.Value, intensity) };

        if (o.ScalpMinAtrMultiplier.HasValue)
        {
            var targetScalpAtr = p.ScalpMinAtr * o.ScalpMinAtrMultiplier.Value;
            adapted = adapted with { ScalpMinAtr = Lerp(p.ScalpMinAtr, targetScalpAtr, intensity) };
        }

        // Issue #9: Apply neutral regime policy override at any positive intensity.
        // Previously used a cliff threshold at 0.5 which caused non-intuitive jumps.
        // Since NeutralRegimePolicy is an enum (not continuously scalable), apply it
        // whenever the overlay system is active (intensity > 0).
        if (o.NeutralRegimePolicyOverride.HasValue && intensity > 0m)
            adapted = adapted with { NeutralRegimePolicy = o.NeutralRegimePolicyOverride.Value };

        return adapted;
    }

    private static decimal Lerp(decimal from, decimal to, decimal t) => from + (to - from) * t;

    // ════════════════════════════════════════════════════
    //  SEED OVERLAY PROFILES PER DIMENSION
    // ════════════════════════════════════════════════════

    private static ParameterOverlay? GetVolatilityOverlay(VolatilityTier tier) => tier switch
    {
        VolatilityTier.LOW => new ParameterOverlay
        {
            ConfidenceBuyThresholdDelta = 2,
            ConfidenceSellThresholdDelta = 2,
            PullbackZonePctOverride = 0.0036m,
            MinAtrThresholdMultiplier = 0.5m,
            BodyRatioMinOverride = 0.28m,
            ScalpMinAtrMultiplier = 0.5m,
            OverlaySource = "proactive-volatility"
        },
        VolatilityTier.HIGH => new ParameterOverlay
        {
            ConfidenceBuyThresholdDelta = -5,
            ConfidenceSellThresholdDelta = -5,
            PullbackZonePctOverride = 0.006m,
            MinAtrThresholdMultiplier = 1.2m,
            ConflictingScoreGapDelta = 2,
            OverlaySource = "proactive-volatility"
        },
        VolatilityTier.EXTREME => new ParameterOverlay
        {
            ConfidenceBuyThresholdDelta = 8,
            ConfidenceSellThresholdDelta = 8,
            NeutralRegimePolicyOverride = NeutralRegimePolicy.BlockAllEntriesInNeutral,
            ScalpCooldownBarsDelta = 5,
            MlMinWinProbabilityDelta = 0.05m,
            OverlaySource = "proactive-volatility"
        },
        _ => null // NORMAL: no overlay
    };

    private static ParameterOverlay? GetTrendOverlay(TrendStrength tier) => tier switch
    {
        TrendStrength.WEAK => new ParameterOverlay
        {
            ConfidenceBuyThresholdDelta = 1,
            ConfidenceSellThresholdDelta = 1,
            NeutralRegimePolicyOverride = NeutralRegimePolicy.AllowReducedRiskEntriesInNeutral,
            MlWeakContextBumpDelta = 0.01m,
            OverlaySource = "proactive-trend"
        },
        TrendStrength.STRONG => new ParameterOverlay
        {
            ConfidenceBuyThresholdDelta = -3,
            ConfidenceSellThresholdDelta = -3,
            NeutralRegimePolicyOverride = NeutralRegimePolicy.AllowReducedRiskEntriesInNeutral,
            MaxRecoveredRegimeAgeBarsDelta = 2,
            OverlaySource = "proactive-trend"
        },
        _ => null // MODERATE: no overlay
    };

    private static ParameterOverlay? GetSessionOverlay(TradingSession session) => session switch
    {
        TradingSession.ASIA => new ParameterOverlay
        {
            ConfidenceBuyThresholdDelta = 1,
            ConfidenceSellThresholdDelta = 1,
            ScalpCooldownBarsDelta = 3,
            MlMinWinProbabilityDelta = 0.03m,
            VolumeMultiplierMinOverride = 0.5m,
            OverlaySource = "proactive-session"
        },
        TradingSession.OVERLAP => new ParameterOverlay
        {
            ConfidenceBuyThresholdDelta = -3,
            ConfidenceSellThresholdDelta = -3,
            PullbackZonePctOverride = 0.005m,
            OverlaySource = "proactive-session"
        },
        _ => null // LONDON, NEW_YORK: no overlay
    };

    private static ParameterOverlay? GetSpreadOverlay(SpreadQuality quality) => quality switch
    {
        SpreadQuality.TIGHT => new ParameterOverlay
        {
            MinAtrThresholdMultiplier = 0.8m,
            OverlaySource = "proactive-spread"
        },
        SpreadQuality.WIDE => new ParameterOverlay
        {
            MinAtrThresholdMultiplier = 1.15m,
            OverlaySource = "proactive-spread"
        },
        _ => null // NORMAL: no overlay
    };

    private static ParameterOverlay? GetVolumeOverlay(VolumeTier tier) => tier switch
    {
        // Issue #11: DRY markets should be MORE conservative, not less.
        // Raise the volume gate (require stronger volume confirmation) and tighten confidence.
        VolumeTier.DRY => new ParameterOverlay
        {
            ConfidenceBuyThresholdDelta = 1,
            ConfidenceSellThresholdDelta = 1,
            VolumeMultiplierMinOverride = 0.85m,
            BodyRatioMinOverride = 0.32m,
            OverlaySource = "proactive-volume"
        },
        VolumeTier.ACTIVE => new ParameterOverlay
        {
            VolumeMultiplierMinOverride = 1.0m,
            BodyRatioMinOverride = 0.25m,
            OverlaySource = "proactive-volume"
        },
        _ => null // NORMAL: no overlay
    };
}
