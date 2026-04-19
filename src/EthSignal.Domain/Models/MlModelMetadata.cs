namespace EthSignal.Domain.Models;

/// <summary>ML model lifecycle status.</summary>
public enum MlModelStatus
{
    Training,
    Candidate,
    Shadow,
    Active,
    Retired
}

/// <summary>
/// Metadata for a trained ML model artifact.
/// Tracks training details, validation metrics, feature set, and lifecycle state.
/// </summary>
public sealed record MlModelMetadata
{
    public required long Id { get; init; }
    public required string ModelType { get; init; }   // "outcome_predictor", "confidence_recalibrator", "threshold_model"
    public required string ModelVersion { get; init; }
    public required string FilePath { get; init; }
    public required string FileFormat { get; init; }   // "onnx" or "mlnet"

    // ─── Training metadata ──────────────────────────────
    public required DateTimeOffset TrainStartUtc { get; init; }
    public required DateTimeOffset TrainEndUtc { get; init; }
    public required int TrainingSampleCount { get; init; }
    public required int FeatureCount { get; init; }
    public required string FeatureListJson { get; init; }

    // ─── Validation metrics ─────────────────────────────
    public decimal AucRoc { get; init; }
    public decimal BrierScore { get; init; }
    public decimal ExpectedCalibrationError { get; init; }
    public decimal LogLoss { get; init; }
    public required string FoldMetricsJson { get; init; }

    // ─── Feature importance ─────────────────────────────
    public required string FeatureImportanceJson { get; init; }

    // ─── Regime scope ────────────────────────────────────────
    /// <summary>
    /// Market-regime scope this model was trained on.
    /// "ALL" = global model trained on all regimes (default fallback).
    /// "BEARISH" / "BULLISH" / "NEUTRAL" = regime-specific specialist.
    /// MlInferenceService loads all four slots and routes by current regime label.
    /// </summary>
    public string RegimeScope { get; init; } = "ALL";

    // ─── Lifecycle ──────────────────────────────────────
    public required MlModelStatus Status { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ActivatedAtUtc { get; init; }
    public DateTimeOffset? RetiredAtUtc { get; init; }
    public string? RetiredReason { get; init; }
}
