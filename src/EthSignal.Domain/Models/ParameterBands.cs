namespace EthSignal.Domain.Models;

/// <summary>
/// Hard min/max safety bands for every adaptively-tunable parameter.
/// No adaptive overlay can push a parameter outside these bounds.
/// Risk-management and structural parameters are never modified.
/// </summary>
public static class ParameterBands
{
    // ─── Confidence thresholds ──────────────────────────
    public const int ConfidenceBuyMin = 45;
    public const int ConfidenceBuyMax = 75;
    public const int ConfidenceSellMin = 45;
    public const int ConfidenceSellMax = 75;

    // ─── Pullback zone ──────────────────────────────────
    public const decimal PullbackZonePctMin = 0.002m;
    public const decimal PullbackZonePctMax = 0.010m;

    // ─── ATR threshold ──────────────────────────────────
    public const decimal MinAtrThresholdMin = 0.3m;
    public const decimal MinAtrThresholdMax = 2.0m;

    // ─── Volume / Body / Spread gates ───────────────────
    public const decimal VolumeMultiplierMinMin = 0.3m;
    public const decimal VolumeMultiplierMinMax = 1.5m;
    public const decimal BodyRatioMinMin = 0.15m;
    public const decimal BodyRatioMinMax = 0.5m;

    // ─── Conflict gap ───────────────────────────────────
    public const int ConflictingScoreGapMin = 0;
    public const int ConflictingScoreGapMax = 10;

    // ─── ML gates ───────────────────────────────────────
    public const decimal MlMinWinProbabilityMin = 0.45m;
    public const decimal MlMinWinProbabilityMax = 0.70m;
    public const decimal MlAccuracyFirstMinWinProbMin = 0.50m;
    public const decimal MlAccuracyFirstMinWinProbMax = 0.65m;
    public const decimal MlWeakContextBumpMin = 0.00m;
    public const decimal MlWeakContextBumpMax = 0.10m;

    // ─── Regime freshness ───────────────────────────────
    public const int MaxRecoveredRegimeAgeBarsMin = 3;
    public const int MaxRecoveredRegimeAgeBarsMax = 12;

    // ─── Scalping ───────────────────────────────────────
    public const int ScalpCooldownBarsMin = 2;
    public const int ScalpCooldownBarsMax = 15;
    public const int ScalpConfidenceThresholdMin = 45;
    public const int ScalpConfidenceThresholdMax = 80;
    public const decimal ScalpMinAtrMin = 0.1m;
    public const decimal ScalpMinAtrMax = 1.0m;

    /// <summary>
    /// Clamp all tunable parameters in <paramref name="adapted"/> to their safe bands,
    /// copying all protected (risk/structural) parameters from <paramref name="baseParams"/>.
    /// </summary>
    public static StrategyParameters Clamp(StrategyParameters adapted, StrategyParameters baseParams)
    {
        return adapted with
        {
            // ── Clamp tunable fields ────────────────────
            ConfidenceBuyThreshold = Math.Clamp(adapted.ConfidenceBuyThreshold, ConfidenceBuyMin, ConfidenceBuyMax),
            ConfidenceSellThreshold = Math.Clamp(adapted.ConfidenceSellThreshold, ConfidenceSellMin, ConfidenceSellMax),
            PullbackZonePct = Math.Clamp(adapted.PullbackZonePct, PullbackZonePctMin, PullbackZonePctMax),
            MinAtrThreshold = Math.Clamp(adapted.MinAtrThreshold, MinAtrThresholdMin, MinAtrThresholdMax),
            VolumeMultiplierMin = Math.Clamp(adapted.VolumeMultiplierMin, VolumeMultiplierMinMin, VolumeMultiplierMinMax),
            BodyRatioMin = Math.Clamp(adapted.BodyRatioMin, BodyRatioMinMin, BodyRatioMinMax),
            ConflictingScoreGap = Math.Clamp(adapted.ConflictingScoreGap, ConflictingScoreGapMin, ConflictingScoreGapMax),
            MlMinWinProbability = Math.Clamp(adapted.MlMinWinProbability, MlMinWinProbabilityMin, MlMinWinProbabilityMax),
            MlAccuracyFirstMinWinProbability = Math.Clamp(adapted.MlAccuracyFirstMinWinProbability, MlAccuracyFirstMinWinProbMin, MlAccuracyFirstMinWinProbMax),
            MlWeakContextMinWinProbabilityBump = Math.Clamp(adapted.MlWeakContextMinWinProbabilityBump, MlWeakContextBumpMin, MlWeakContextBumpMax),
            MaxRecoveredRegimeAgeBars = Math.Clamp(adapted.MaxRecoveredRegimeAgeBars, MaxRecoveredRegimeAgeBarsMin, MaxRecoveredRegimeAgeBarsMax),
            ScalpCooldownBars = Math.Clamp(adapted.ScalpCooldownBars, ScalpCooldownBarsMin, ScalpCooldownBarsMax),
            ScalpConfidenceThreshold = Math.Clamp(adapted.ScalpConfidenceThreshold, ScalpConfidenceThresholdMin, ScalpConfidenceThresholdMax),
            ScalpMinAtr = Math.Clamp(adapted.ScalpMinAtr, ScalpMinAtrMin, ScalpMinAtrMax),

            // ── Force-copy protected fields from base ───
            AccountBalanceUsd = baseParams.AccountBalanceUsd,
            RiskPerTradePercent = baseParams.RiskPerTradePercent,
            HardMaxRiskPercent = baseParams.HardMaxRiskPercent,
            DailyLossCapPercent = baseParams.DailyLossCapPercent,
            MaxConsecutiveLossesPerDay = baseParams.MaxConsecutiveLossesPerDay,
            MaxOpenPositions = baseParams.MaxOpenPositions,
            StopAtrMultiplier = baseParams.StopAtrMultiplier,
            TargetRMultiple = baseParams.TargetRMultiple,
            MinRiskRewardAfterRounding = baseParams.MinRiskRewardAfterRounding,
            LiveEntrySlippageBufferPct = baseParams.LiveEntrySlippageBufferPct,
            MaxSpreadPct = baseParams.MaxSpreadPct,
            // Indicator periods
            EmaFastPeriod = baseParams.EmaFastPeriod,
            EmaSlowPeriod = baseParams.EmaSlowPeriod,
            RsiPeriod = baseParams.RsiPeriod,
            MacdFast = baseParams.MacdFast,
            MacdSlow = baseParams.MacdSlow,
            MacdSignalPeriod = baseParams.MacdSignalPeriod,
            AtrPeriod = baseParams.AtrPeriod,
            AdxPeriod = baseParams.AdxPeriod,
            VolumeLookback = baseParams.VolumeLookback,
            WarmUpPeriod = baseParams.WarmUpPeriod,
            // Timeframes
            TimeframePrimary = baseParams.TimeframePrimary,
            TimeframeBias = baseParams.TimeframeBias,
            // Structural
            StrategyVersion = baseParams.StrategyVersion,
            ObjectiveFunctionVersion = baseParams.ObjectiveFunctionVersion,
            OutcomeTimeoutBars = baseParams.OutcomeTimeoutBars,
            GapBlockLookbackBars = baseParams.GapBlockLookbackBars,
        };
    }
}
