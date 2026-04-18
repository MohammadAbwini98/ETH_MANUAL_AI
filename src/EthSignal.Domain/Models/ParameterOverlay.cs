namespace EthSignal.Domain.Models;

/// <summary>
/// Defines delta adjustments or absolute overrides for tunable strategy parameters.
/// Null fields mean "no adjustment — use base value."
/// Multiple overlays are merged per the rules in AdaptiveOverlayResolver.
/// </summary>
public sealed record ParameterOverlay
{
    // ─── Delta fields (summed across overlays) ──────────
    public int? ConfidenceBuyThresholdDelta { get; init; }
    public int? ConfidenceSellThresholdDelta { get; init; }
    public int? ConflictingScoreGapDelta { get; init; }
    public int? MaxRecoveredRegimeAgeBarsDelta { get; init; }
    public int? ScalpCooldownBarsDelta { get; init; }
    public int? ScalpConfidenceThresholdDelta { get; init; }
    public decimal? MlMinWinProbabilityDelta { get; init; }
    public decimal? MlAccuracyFirstMinWinProbDelta { get; init; }
    public decimal? MlWeakContextBumpDelta { get; init; }

    // ─── Override fields (most restrictive wins) ────────
    public decimal? PullbackZonePctOverride { get; init; }
    public decimal? MinAtrThresholdMultiplier { get; init; }
    public decimal? VolumeMultiplierMinOverride { get; init; }
    public decimal? BodyRatioMinOverride { get; init; }
    public decimal? ScalpMinAtrMultiplier { get; init; }

    // ─── Enum overrides (most restrictive wins) ─────────
    public NeutralRegimePolicy? NeutralRegimePolicyOverride { get; init; }

    // ─── Metadata ───────────────────────────────────────
    public string OverlaySource { get; init; } = "default";
}
