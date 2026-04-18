using System.Text.Json;

namespace EthSignal.Domain.Models;

/// <summary>Standardized rejection reason codes per FR-2.</summary>
public enum RejectReasonCode
{
    REGIME_NEUTRAL,
    REGIME_BULLISH_REQUIRED,
    REGIME_BEARISH_REQUIRED,
    PULLBACK_NOT_VALID,
    RSI_OUT_OF_RANGE,
    VWAP_FILTER_FAILED,
    ADX_TOO_LOW,
    SPREAD_TOO_WIDE,
    BODY_RATIO_TOO_SMALL,
    MACD_CONFIRMATION_FAILED,
    RISK_FILTER_FAILED,
    MISSING_HTF_CONTEXT,
    STALE_HTF_CONTEXT,
    NO_ENTRY_CONDITION,
    SCORE_BELOW_THRESHOLD,
    SESSION_LIMIT_REACHED,
    CONFLICTING_SIGNALS,
    ATR_TOO_LOW,
    ML_GATE_FAILED,
    RISK_MANAGER_BLOCKED,
    VOLUME_TOO_LOW,
    MAX_OPEN_PER_TIMEFRAME,
    MAX_OPEN_PER_DIRECTION,
    SLOT_CAPACITY_REACHED
}

/// <summary>FR-1: Signal lifecycle states — explicit multi-stage lifecycle for each evaluated opportunity.</summary>
public enum SignalLifecycleState
{
    /// <summary>Opportunity was evaluated by the signal engine.</summary>
    EVALUATED,
    /// <summary>Engine produced a directional candidate (BUY/SELL) above threshold.</summary>
    CANDIDATE_CREATED,
    /// <summary>ML gate rejected the candidate.</summary>
    ML_FILTERED,
    /// <summary>Risk manager or position capacity blocked the candidate.</summary>
    RISK_BLOCKED,
    /// <summary>Session-level limits (drawdown, consecutive losses) blocked the candidate.</summary>
    SESSION_BLOCKED,
    /// <summary>Signal was accepted and persisted to the signal table.</summary>
    PERSISTED,
    /// <summary>Signal execution was confirmed (future: order placed on exchange).</summary>
    EXECUTED,
    /// <summary>Signal outcome was resolved (WIN/LOSS/EXPIRED).</summary>
    CLOSED
}

/// <summary>FR-4: Decision origin — distinguishes partial running candle from closed candle evaluation.</summary>
public enum DecisionOrigin
{
    CLOSED_BAR,
    PARTIAL_RUNNING,
    CONFIRMED_RUNNING,
    SCALP_1M,
    STARTUP_WARM
}

/// <summary>FR-8: Distinguishes strategy rejection from operational/contextual blockage.</summary>
public enum OutcomeCategory
{
    SIGNAL_GENERATED,
    STRATEGY_NO_TRADE,
    OPERATIONAL_BLOCKED,
    CONTEXT_NOT_READY
}

/// <summary>Decision source for warm-start vs live vs replay tagging.</summary>
public enum SourceMode
{
    LIVE,
    STARTUP_WARM,
    HISTORICAL_REPLAY
}

/// <summary>TF-3: Neutral regime gating policy — externally configurable per FR-7.</summary>
public enum NeutralRegimePolicy
{
    BlockAllEntriesInNeutral,
    AllowReducedRiskEntriesInNeutral,
    AllowCountertrendScalpEntriesInNeutral,
    AllowMlGatedEntriesInNeutral
}

/// <summary>
/// TF-1: First-class structured decision result.
/// Canonical source for logging, persistence, dashboarding, and tests.
/// Every live 5m evaluation produces exactly one of these, regardless of outcome.
/// </summary>
public sealed record SignalDecision
{
    public Guid DecisionId { get; init; } = Guid.NewGuid();
    public required string Symbol { get; init; }
    public required string Timeframe { get; init; }
    public required DateTimeOffset DecisionTimeUtc { get; init; }
    public required DateTimeOffset BarTimeUtc { get; init; }

    /// <summary>LONG / SHORT / NO_TRADE</summary>
    public required SignalDirection DecisionType { get; init; }

    /// <summary>FR-8: STRATEGY_NO_TRADE / OPERATIONAL_BLOCKED / CONTEXT_NOT_READY</summary>
    public required OutcomeCategory OutcomeCategory { get; init; }

    /// <summary>FR-1: Explicit lifecycle state for this evaluation opportunity.</summary>
    public SignalLifecycleState LifecycleState { get; init; } = SignalLifecycleState.EVALUATED;

    /// <summary>FR-1: Final block reason when lifecycle did not reach PERSISTED (null if persisted).</summary>
    public string? FinalBlockReason { get; init; }

    /// <summary>FR-4: Decision origin — closed bar, partial running, scalp, startup warm.</summary>
    public DecisionOrigin Origin { get; init; } = DecisionOrigin.CLOSED_BAR;

    /// <summary>FR-16: Correlation key spanning feature extraction, prediction, decision, signal.</summary>
    public Guid EvaluationId { get; init; } = Guid.NewGuid();

    /// <summary>FR-8: JSON snapshot of the effective runtime parameters used for this decision.</summary>
    public string? EffectiveRuntimeParametersJson { get; init; }

    /// <summary>The regime used for this evaluation (null if unavailable).</summary>
    public Regime? UsedRegime { get; init; }

    /// <summary>Timestamp of the regime bar used.</summary>
    public DateTimeOffset? UsedRegimeTimestamp { get; init; }

    /// <summary>FR-2: Machine-readable rejection reason codes.</summary>
    public required IReadOnlyList<RejectReasonCode> ReasonCodes { get; init; }

    /// <summary>Human-readable reason details (for logs and display).</summary>
    public required IReadOnlyList<string> ReasonDetails { get; init; }

    /// <summary>Confidence score from signal engine evaluation.</summary>
    public int ConfidenceScore { get; init; }

    /// <summary>Key indicator values used at decision time.</summary>
    public required IReadOnlyDictionary<string, decimal> IndicatorSnapshot { get; init; }

    /// <summary>Active parameter set version / id.</summary>
    public string? ParameterSetId { get; init; }

    /// <summary>LIVE / STARTUP_WARM / HISTORICAL_REPLAY</summary>
    public SourceMode SourceMode { get; init; } = SourceMode.LIVE;

    /// <summary>Serialize reason codes to JSON for storage.</summary>
    public string ReasonCodesJson =>
        JsonSerializer.Serialize(ReasonCodes.Select(r => r.ToString()).ToList());

    /// <summary>Serialize reason details to JSON for storage.</summary>
    public string ReasonDetailsJson =>
        JsonSerializer.Serialize(ReasonDetails);

    /// <summary>Serialize indicator snapshot to JSON for storage.</summary>
    public string IndicatorsJson =>
        JsonSerializer.Serialize(IndicatorSnapshot);

    /// <summary>ML prediction at decision time (null if ML disabled).</summary>
    public MlPrediction? MlPrediction { get; init; }

    /// <summary>Blended confidence (rule-based * (1-w) + ML * w).</summary>
    public int? BlendedConfidence { get; init; }

    /// <summary>Effective threshold used (may differ from parameter if dynamic).</summary>
    public int? EffectiveThreshold { get; init; }

    /// <summary>Candidate direction preserved for future ML rescue of sub-threshold signals.</summary>
    public SignalDirection? CandidateDirection { get; init; }

    /// <summary>
    /// Composite market condition class active at decision time
    /// (e.g. NORMAL_MODERATE_NEW_YORK_TIGHT_DRY). Null when adaptive system is disabled
    /// or condition could not be classified (warm-up, missing snapshot).
    /// </summary>
    public string? MarketConditionClass { get; init; }

    /// <summary>
    /// JSON of the effective adapted parameter overlay diffs that were applied for this
    /// decision (vs. the active base parameter set). Null when adaptive system is disabled
    /// or no overlay diff was produced.
    /// </summary>
    public string? AdaptedParametersJson { get; init; }
}

/// <summary>FR-4: Regime recovery provenance record.</summary>
public sealed record RegimeRecoveryInfo
{
    public required string Symbol { get; init; }
    public Regime? RecoveredRegime { get; init; }
    public DateTimeOffset? RegimeBarTimeUtc { get; init; }
    public required DateTimeOffset RecoveryTimeUtc { get; init; }
    public int? AgeBars { get; init; }
    public required string Freshness { get; init; } // FRESH / STALE / UNAVAILABLE
}
