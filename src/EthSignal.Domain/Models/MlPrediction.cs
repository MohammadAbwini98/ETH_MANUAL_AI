namespace EthSignal.Domain.Models;

/// <summary>ML operating mode.</summary>
public enum MlMode { DISABLED, SHADOW, ACTIVE }

/// <summary>FR-9: Explicit ML inference mode — distinguishes heuristic fallback from trained model.</summary>
public enum MlInferenceMode
{
    /// <summary>No ML model loaded; using heuristic fallback logic.</summary>
    HEURISTIC_FALLBACK,
    /// <summary>Trained model loaded but running in shadow (log-only, no gating).</summary>
    TRAINED_SHADOW,
    /// <summary>Trained model loaded, actively filtering/gating signals.</summary>
    TRAINED_ACTIVE
}

/// <summary>
/// Result of ML inference at signal evaluation time.
/// Persisted for audit trail and A/B comparison.
/// </summary>
public sealed record MlPrediction
{
    public required Guid PredictionId { get; init; }
    public required Guid EvaluationId { get; init; }
    public Guid? SignalId { get; init; }
    public string? Timeframe { get; init; }
    public string? LinkStatus { get; init; }
    public required string ModelVersion { get; init; }
    public required string ModelType { get; init; }

    /// <summary>Raw model probability before recalibration (0.0 to 1.0).</summary>
    public decimal RawWinProbability { get; init; }

    /// <summary>Calibrated P(WIN) used for gating and diagnostics (0.0 to 1.0).</summary>
    public decimal CalibratedWinProbability { get; init; }

    /// <summary>Model certainty in its own predicted class (0-100).</summary>
    public int PredictionConfidence { get; init; }

    /// <summary>Dynamic threshold for current conditions.</summary>
    public int RecommendedThreshold { get; init; }

    /// <summary>Expected value: P(WIN)*avgWinR - P(LOSS)*avgLossR</summary>
    public decimal ExpectedValueR { get; init; }

    /// <summary>Inference latency in microseconds.</summary>
    public int InferenceLatencyUs { get; init; }

    /// <summary>Whether this prediction influenced the trade decision.</summary>
    public bool IsActive { get; init; }

    /// <summary>Mode: SHADOW (log only) or ACTIVE (influences decision).</summary>
    public required MlMode Mode { get; init; }

    /// <summary>FR-9: Explicit inference mode — heuristic fallback vs trained model.</summary>
    public MlInferenceMode InferenceMode { get; init; } = MlInferenceMode.HEURISTIC_FALLBACK;

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
