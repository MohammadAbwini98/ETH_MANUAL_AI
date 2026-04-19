using System.Text.Json;

namespace EthSignal.Domain.Models;

/// <summary>
/// B-07: Complete externalized strategy parameter set.
/// Every threshold that affects trading decisions lives here.
/// No hidden constants should remain in engine code.
/// </summary>
public sealed record StrategyParameters
{
    // ─── Identity ────────────────────────────────────────
    public string StrategyVersion { get; init; } = "v3.1";

    // ─── Timeframes ─────────────────────────────────────
    public string TimeframePrimary { get; init; } = "5m";
    public string TimeframeBias { get; init; } = "15m";

    // ─── Indicator Periods ──────────────────────────────
    public int EmaFastPeriod { get; init; } = 20;
    public int EmaSlowPeriod { get; init; } = 50;
    public int RsiPeriod { get; init; } = 14;
    public int MacdFast { get; init; } = 12;
    public int MacdSlow { get; init; } = 26;
    public int MacdSignalPeriod { get; init; } = 9;
    public int AtrPeriod { get; init; } = 14;
    public int AdxPeriod { get; init; } = 14;
    public int VolumeLookback { get; init; } = 20;
    public int WarmUpPeriod { get; init; } = 50;

    // ─── Regime Thresholds ──────────────────────────────
    public decimal AdxTrendThreshold { get; init; } = 20m;
    public int RegimeSlopeCandles { get; init; } = 3;
    public int MarketStructureLookback { get; init; } = 4;

    // ─── Neutral Regime Policy (FR-7 / TF-3) ───────────
    // Default to fully blocking neutral-regime entries until a dedicated
    // range/mean-reversion strategy exists.
    public NeutralRegimePolicy NeutralRegimePolicy { get; init; } = NeutralRegimePolicy.BlockAllEntriesInNeutral;

    // ─── Regime Freshness (TF-4) ────────────────────────
    public int MaxRecoveredRegimeAgeBars { get; init; } = 6;

    // ─── Warm-Start & Replay Modes ──────────────────────
    public bool WarmStartEvaluateLatestClosed5m { get; init; } = false;
    public bool BackfillReplaySignals { get; init; } = false;

    // ─── Signal Score Weights ───────────────────────────
    public int WeightRegime { get; init; } = 20;
    public int WeightPullback { get; init; } = 20;
    public int WeightRsi { get; init; } = 15;
    public int WeightMacd { get; init; } = 15;
    public int WeightAdx { get; init; } = 10;
    public int WeightVolume { get; init; } = 10;
    public int WeightSpread { get; init; } = 5;
    public int WeightBody { get; init; } = 5;

    // ─── Signal Thresholds ──────────────────────────────
    public int ConfidenceBuyThreshold { get; init; } = 45;
    public int ConfidenceSellThreshold { get; init; } = 45;
    public decimal PullbackZonePct { get; init; } = 0.004m; // 0.4% tolerance (tighter catch)

    // ─── Conflict Ambiguity Guard ────────────────────────
    /// <summary>
    /// When both BUY and SELL scores exceed their thresholds and differ by ≤ this value,
    /// the market is ambiguous and NO_TRADE is emitted. Prevents flip-conflicts.
    /// </summary>
    public int ConflictingScoreGap { get; init; } = 3;

    // ─── 1m Scalping Overrides ──────────────────────────
    /// <summary>Enable 1m scalping signal evaluation on every 1m candle close.</summary>
    public bool ScalpingEnabled { get; init; } = true;
    /// <summary>Minimum confidence score for 1m scalp signals (higher bar than HTF).</summary>
    public int ScalpConfidenceThreshold { get; init; } = 60;
    /// <summary>Minimum ATR for 1m scalp signals (lower than HTF since 1m ATR is smaller).</summary>
    public decimal ScalpMinAtr { get; init; } = 0.3m;
    /// <summary>Stop-loss ATR multiplier for 1m scalps (tighter stops).</summary>
    public decimal ScalpStopAtrMultiplier { get; init; } = 1.0m;
    /// <summary>Target R:R for 1m scalps (faster take-profit).</summary>
    public decimal ScalpTargetRMultiple { get; init; } = 1.5m;
    /// <summary>Minimum 1m closed candles needed before evaluating scalp signals.</summary>
    public int ScalpWarmUpBars { get; init; } = 30;
    /// <summary>Cooldown: minimum bars between 1m scalp signals to avoid over-trading.</summary>
    public int ScalpCooldownBars { get; init; } = 5;

    // ─── RSI Entry Zones ────────────────────────────────
    public decimal RsiBuyMin { get; init; } = 30m;
    public decimal RsiBuyMax { get; init; } = 70m;
    public decimal RsiBuyFallback { get; init; } = 42m;
    public decimal RsiSellMin { get; init; } = 30m;
    public decimal RsiSellMax { get; init; } = 70m;
    public decimal RsiSellFallback { get; init; } = 58m;

    // ─── Volume / Spread / Body Gates ───────────────────
    public decimal VolumeMultiplierMin { get; init; } = 0.8m;
    public decimal MaxSpreadPct { get; init; } = 0.004m;
    public decimal BodyRatioMin { get; init; } = 0.3m;

    // ─── Risk Management ────────────────────────────────
    public decimal AccountBalanceUsd { get; init; } = 50m;
    public decimal RiskPerTradePercent { get; init; } = 0.5m;
    public decimal HardMaxRiskPercent { get; init; } = 1.0m;
    public decimal DailyLossCapPercent { get; init; } = decimal.MaxValue;
    public int MaxConsecutiveLossesPerDay { get; init; } = int.MaxValue;
    public int MaxOpenPositions { get; init; } = int.MaxValue;
    public decimal StopAtrMultiplier { get; init; } = 2.0m;
    public decimal TargetRMultiple { get; init; } = 1.5m;
    public decimal MinAtrThreshold { get; init; } = 0.8m;
    public decimal MinRiskRewardAfterRounding { get; init; } = 1.5m;
    public decimal LiveEntrySlippageBufferPct { get; init; } = 0.002m;

    // ─── FR-2: Scoped Position Capacity ─────────────────
    /// <summary>Maximum open signals per individual timeframe (0 = no per-TF limit, use global).</summary>
    public int MaxOpenPerTimeframe { get; init; } = 0;
    /// <summary>Maximum open signals per direction across all TFs (0 = no per-direction limit).</summary>
    public int MaxOpenPerDirection { get; init; } = 0;

    // ─── Scalp-Specific Session Limits ───────────────────
    /// <summary>
    /// Max consecutive losses before blocking 1m scalp independently of HTF losses.
    /// 0 = fall back to MaxConsecutiveLossesPerDay (shared limit).
    /// </summary>
    public int ScalpMaxConsecutiveLossesPerDay { get; init; } = int.MaxValue;

    /// <summary>
    /// Max daily drawdown percent before blocking 1m scalp independently of HTF drawdown.
    /// 0 = fall back to DailyLossCapPercent (shared limit).
    /// </summary>
    public decimal ScalpDailyMaxDrawdownPercent { get; init; } = 3.0m;

    // ─── Outcome ────────────────────────────────────────
    public int OutcomeTimeoutBars { get; init; } = 60;
    public int GapBlockLookbackBars { get; init; } = 2; // multiplier × timeframe minutes

    // ─── FR-3: Timeframe-Aware Timeout Policy ───────────
    /// <summary>Outcome timeout for 1m scalp signals (bars). 0 = use OutcomeTimeoutBars.</summary>
    public int ScalpTimeoutBars { get; init; } = 25;
    /// <summary>Outcome timeout for intraday signals (5m/15m/30m bars). 0 = use OutcomeTimeoutBars.</summary>
    public int IntradayTimeoutBars { get; init; } = 90;
    /// <summary>Outcome timeout for higher-TF signals (1h/4h bars). 0 = use OutcomeTimeoutBars.</summary>
    public int HigherTfTimeoutBars { get; init; } = 30;

    // ─── FR-4: Partial Running-Candle Maturity ──────────
    /// <summary>Minimum bar maturity ratio for running-candle eval on fast TFs (1m/5m). 0.0–1.0, default 60%.</summary>
    public decimal RunningCandleMaturityFastTf { get; init; } = 0.6m;
    /// <summary>Minimum bar maturity ratio for running-candle eval on slow TFs (15m+). 0.0–1.0, default 50%.</summary>
    public decimal RunningCandleMaturitySlowTf { get; init; } = 0.5m;

    // ─── Objective (for optimizer) ──────────────────────
    public string ObjectiveFunctionVersion { get; init; } = "v1.0";

    // ─── ML Enhancement ─────────────────────────────────
    public MlMode MlMode { get; init; } = MlMode.SHADOW;
    /// <summary>
    /// When true, startup may promote MlMode from SHADOW to ACTIVE automatically
    /// after a healthy trained model is loaded. Default is false so SHADOW
    /// remains the safe default unless explicitly opted in.
    /// </summary>
    public bool MlAutoActivateOnStartup { get; init; } = false;
    public decimal MlMinWinProbability { get; init; } = 0.55m;
    public decimal MlConfidenceBlendWeight { get; init; } = 0.5m;
    public bool MlDynamicThresholdsEnabled { get; init; } = false;
    public int MlDynamicThresholdMin { get; init; } = 40;
    public int MlDynamicThresholdMax { get; init; } = 90;
    public int MlDynamicThresholdMaxDelta { get; init; } = 5;
    public int MlRetrainSignalThreshold { get; init; } = 100;
    public int MlRetrainMaxDays { get; init; } = 7;
    public int MlShadowEvalCount { get; init; } = 50;
    public decimal MlDriftAucThreshold { get; init; } = 0.52m;
    public decimal MlDriftBrierThreshold { get; init; } = 0.28m;
    public bool MlOverrideMandatoryGates { get; init; } = false;

    // ─── Accuracy-First Mode ────────────────────────────
    /// <summary>
    /// Accuracy-first mode: favor precision over frequency. When enabled, the
    /// runtime uses a stricter ML win-probability gate, refuses to relax
    /// thresholds when the loaded model is unhealthy, and applies additional
    /// context-aware de-risking in weak regimes / low ADX / Asia session.
    /// </summary>
    public bool MlAccuracyFirstMode { get; init; } = true;

    /// <summary>
    /// Effective minimum calibrated win probability when accuracy-first mode
    /// is active. Overrides <see cref="MlMinWinProbability"/> in that case.
    /// Reduced from 0.60 to 0.55 to match the standard gate while the ML model
    /// is running in heuristic-fallback mode (insufficient training data).
    /// Once a real trained ONNX model is loaded this can be raised back to 0.60.
    /// </summary>
    public decimal MlAccuracyFirstMinWinProbability { get; init; } = 0.55m;

    /// <summary>
    /// Extra bump to the ML win-probability gate applied in "weak context"
    /// setups: NEUTRAL regime, low ADX, or weak Asia session. Additive on top
    /// of the accuracy-first base probability (e.g. 0.60 + 0.03 = 0.63).
    /// </summary>
    public decimal MlWeakContextMinWinProbabilityBump { get; init; } = 0.03m;

    /// <summary>
    /// ADX value below which the market is considered "weak trend" and the
    /// accuracy-first bump is applied.
    /// </summary>
    public decimal AccuracyFirstLowAdxThreshold { get; init; } = 18m;

    /// <summary>
    /// When true AND the loaded inference model is the heuristic fallback or
    /// otherwise unhealthy, dynamic threshold relaxation is forcibly disabled
    /// regardless of <see cref="MlDynamicThresholdsEnabled"/>. Protects against
    /// lowering the bar while running without a trained model.
    /// </summary>
    public bool MlDisableDynamicThresholdsWhenUnhealthy { get; init; } = true;

    // ─── Adaptive Parameter System ──────────────────────
    /// <summary>Enable market-adaptive parameter adjustment.</summary>
    public bool AdaptiveParametersEnabled { get; init; } = true;

    /// <summary>Enable retrospective (outcome-driven) overlay refinement.</summary>
    public bool AdaptiveRetrospectiveEnabled { get; init; } = true;

    /// <summary>Minimum resolved outcomes per condition before retrospective kicks in.</summary>
    public int AdaptiveRetrospectiveMinOutcomes { get; init; } = 15;

    /// <summary>Rolling window size for per-condition outcome tracking.</summary>
    public int AdaptiveRetrospectiveWindowSize { get; init; } = 30;

    /// <summary>
    /// Master knob 0.0–1.0 controlling overlay intensity. 0.0 = base params only,
    /// 1.0 = full overlay. Useful for gradual rollout.
    /// </summary>
    public decimal AdaptiveOverlayIntensity { get; init; } = 1.0m;

    // ─── Exit Engine (Structure-Aware TP/SL) ────────────
    /// <summary>Max SL distance as % of entry price. Rejects setups with too-wide stops.</summary>
    public decimal ExitMaxStopDistancePct { get; init; } = 0.05m;
    /// <summary>Min SL distance as % of entry price. Prevents unrealistically tight stops.</summary>
    public decimal MinStopDistancePct { get; init; } = 0.002m;

    /// <summary>Multi-target TP1 in R-multiples (early partial profit).</summary>
    public decimal ExitTp1RMultiple { get; init; } = 1.0m;
    /// <summary>Multi-target TP2 in R-multiples (structure target).</summary>
    public decimal ExitTp2RMultiple { get; init; } = 2.0m;
    /// <summary>Multi-target TP3 in R-multiples (trailing runner).</summary>
    public decimal ExitTp3RMultiple { get; init; } = 3.0m;

    /// <summary>TP multiplier when regime is trending in signal direction.</summary>
    public decimal ExitTrendingTpMultiplier { get; init; } = 1.3m;
    /// <summary>SL multiplier when regime is trending in signal direction.</summary>
    public decimal ExitTrendingSlMultiplier { get; init; } = 1.1m;
    /// <summary>TP multiplier in ranging/neutral regime.</summary>
    public decimal ExitRangingTpMultiplier { get; init; } = 1.2m;
    /// <summary>SL multiplier in ranging/neutral regime.</summary>
    public decimal ExitRangingSlMultiplier { get; init; } = 0.8m;

    /// <summary>TP boost for high-confidence signals.</summary>
    public decimal ExitHighConfidenceTpBoost { get; init; } = 1.2m;
    /// <summary>TP reduction for low-confidence signals.</summary>
    public decimal ExitLowConfidenceTpReduce { get; init; } = 0.8m;
    /// <summary>Confidence score >= this gets TP boost.</summary>
    public int ExitHighConfidenceThreshold { get; init; } = 80;
    /// <summary>Confidence score &lt;= this gets TP reduction.</summary>
    public int ExitLowConfidenceThreshold { get; init; } = 55;

    /// <summary>Scalp min ATR multiplier for TP projection range.</summary>
    public decimal ExitScalpMinAtrTpMultiplier { get; init; } = 0.8m;
    /// <summary>Scalp max ATR multiplier for TP projection range.</summary>
    public decimal ExitScalpMaxAtrTpMultiplier { get; init; } = 1.5m;
    /// <summary>Intraday min ATR multiplier for TP projection range.</summary>
    public decimal ExitIntradayMinAtrTpMultiplier { get; init; } = 1.5m;
    /// <summary>Intraday max ATR multiplier for TP projection range.</summary>
    public decimal ExitIntradayMaxAtrTpMultiplier { get; init; } = 3.0m;

    /// <summary>Scalp TP1 R-multiple (tighter than HTF).</summary>
    public decimal ExitScalpTp1RMultiple { get; init; } = 0.8m;
    /// <summary>Scalp TP2 R-multiple.</summary>
    public decimal ExitScalpTp2RMultiple { get; init; } = 1.2m;
    /// <summary>Scalp TP3 R-multiple.</summary>
    public decimal ExitScalpTp3RMultiple { get; init; } = 1.8m;

    /// <summary>Number of candles of lookback history for structure-aware exit analysis.</summary>
    public int ExitStructureLookbackBars { get; init; } = 50;

    // ─── Helpers ────────────────────────────────────────

    /// <summary>Build a RiskPolicy from these parameters (backwards compat).</summary>
    public RiskPolicy ToRiskPolicy() => new()
    {
        AccountBalanceUsd = AccountBalanceUsd,
        RiskPercentPerTrade = RiskPerTradePercent,
        HardMaxRiskPercent = HardMaxRiskPercent,
        DailyMaxDrawdownPercent = DailyLossCapPercent,
        MaxConsecutiveLossesPerDay = MaxConsecutiveLossesPerDay,
        MaxOpenPositions = MaxOpenPositions,
        AtrMultiplier = StopAtrMultiplier,
        RewardToRisk = TargetRMultiple,
        MinAtrThreshold = MinAtrThreshold,
        MaxSpreadPct = MaxSpreadPct,
        MinRiskRewardAfterRounding = MinRiskRewardAfterRounding,
        ScalpMaxConsecutiveLossesPerDay = ScalpMaxConsecutiveLossesPerDay,
        ScalpDailyMaxDrawdownPercent = ScalpDailyMaxDrawdownPercent
    };

    /// <summary>Validate constraints. Returns null if valid, error message if invalid.</summary>
    public string? Validate()
    {
        if (EmaFastPeriod >= EmaSlowPeriod)
            return $"EmaFastPeriod({EmaFastPeriod}) must be < EmaSlowPeriod({EmaSlowPeriod})";
        if (HardMaxRiskPercent < RiskPerTradePercent)
            return $"HardMaxRiskPercent({HardMaxRiskPercent}) must be >= RiskPerTradePercent({RiskPerTradePercent})";
        if (RsiBuyMin >= RsiBuyMax)
            return $"RsiBuyMin({RsiBuyMin}) must be < RsiBuyMax({RsiBuyMax})";
        if (RsiSellMin >= RsiSellMax)
            return $"RsiSellMin({RsiSellMin}) must be < RsiSellMax({RsiSellMax})";
        if (StopAtrMultiplier <= 0)
            return "StopAtrMultiplier must be > 0";
        if (TargetRMultiple <= 0)
            return "TargetRMultiple must be > 0";
        if (LiveEntrySlippageBufferPct < 0)
            return "LiveEntrySlippageBufferPct must be >= 0";
        if (ConfidenceBuyThreshold <= 0 || ConfidenceSellThreshold <= 0)
            return "Confidence thresholds must be > 0";
        if (OutcomeTimeoutBars <= 0)
            return "OutcomeTimeoutBars must be > 0";
        if (IntradayTimeoutBars <= 0)
            return "IntradayTimeoutBars must be > 0";
        if (WarmUpPeriod < EmaSlowPeriod)
            return $"WarmUpPeriod({WarmUpPeriod}) must be >= EmaSlowPeriod({EmaSlowPeriod})";
        if (ExitTp1RMultiple >= ExitTp2RMultiple || ExitTp2RMultiple >= ExitTp3RMultiple)
            return "Exit TP multiples must be strictly ascending";
        if (ExitScalpTp1RMultiple >= ExitScalpTp2RMultiple || ExitScalpTp2RMultiple >= ExitScalpTp3RMultiple)
            return "Scalp exit TP multiples must be strictly ascending";
        if (MinStopDistancePct <= 0)
            return "MinStopDistancePct must be > 0";
        if (MinStopDistancePct >= ExitMaxStopDistancePct)
            return $"MinStopDistancePct({MinStopDistancePct}) must be < ExitMaxStopDistancePct({ExitMaxStopDistancePct})";
        return null;
    }

    /// <summary>Deterministic JSON for hashing.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    });

    /// <summary>Default parameter set.</summary>
    public static readonly StrategyParameters Default = new();
}
