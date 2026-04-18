namespace EthSignal.Infrastructure.Engine.ML;

public static class MlDiagnosticsStatus
{
    public const string Healthy = "HEALTHY";
    public const string Warning = "WARNING";
    public const string Critical = "CRITICAL";
    public const string InsufficientData = "INSUFFICIENT_DATA";
}

public sealed record MlDataDiagnosticsReport
{
    public required DateTimeOffset GeneratedAtUtc { get; init; }
    public required string Symbol { get; init; }
    public required string Timeframe { get; init; }
    public required string OverallStatus { get; init; }
    public required string FeatureVersion { get; init; }
    public bool IsFeatureVersionFallback { get; init; }
    public required MlModelDiagnosticsSummary Model { get; init; }
    public required MlLabelQualityDiagnostics LabelQuality { get; init; }
    public required MlClassBalanceDiagnostics ClassBalance { get; init; }
    public required MlCalibrationDiagnostics Calibration { get; init; }
    public required MlFeatureDriftDiagnostics FeatureDrift { get; init; }
}

public sealed record MlModelDiagnosticsSummary
{
    public string? ModelVersion { get; init; }
    public int? TrainingSampleCount { get; init; }
    public int? FeatureCount { get; init; }
    public int CurrentFeatureCount { get; init; }
    public bool UsesCurrentFeatureContract { get; init; }
    public decimal? AucRoc { get; init; }
    public decimal? BrierScore { get; init; }
    public decimal? ExpectedCalibrationError { get; init; }
    public decimal? LogLoss { get; init; }
    public DateTimeOffset? ActivatedAtUtc { get; init; }
}

public sealed record MlLabelQualityDiagnostics
{
    public required string Status { get; init; }
    public int TotalOutcomes { get; init; }
    public int LabeledOutcomes { get; init; }
    public int PendingOutcomes { get; init; }
    public int ExpiredOutcomes { get; init; }
    public int AmbiguousOutcomes { get; init; }
    public int InconsistentPnlLabels { get; init; }
    public int ConflictingTpSlHits { get; init; }
    public int ClosedTimestampMissing { get; init; }
    public int TotalFeatureSnapshots { get; init; }
    public int LinkedFeatureSnapshots { get; init; }
    public int LabeledFeatureSnapshots { get; init; }
    /// <summary>
    /// Strict training-export-aligned count: rows that the training
    /// export's direct-linked query would actually select (signal_id not
    /// null, link_status=SIGNAL_LINKED, outcome label in WIN/LOSS).
    /// </summary>
    public int TrainableFeatureSnapshots { get; init; }
    public int PendingLinkSnapshots { get; init; }
    public int StalePendingLinkSnapshots { get; init; }
    public int ExpectedNoSignalSnapshots { get; init; }
    public int MlFilteredSnapshots { get; init; }
    public int OperationallyBlockedSnapshots { get; init; }
    public decimal LinkCoveragePct { get; init; }
}

public sealed record MlClassBalanceDiagnostics
{
    public required string Status { get; init; }
    public int LabeledSamples { get; init; }
    public int Wins { get; init; }
    public int Losses { get; init; }
    public decimal WinRate { get; init; }
    public decimal LossToWinRatio { get; init; }
    public bool ReadyForTraining { get; init; }
}

public sealed record MlCalibrationDiagnostics
{
    public required string Status { get; init; }
    public int SampleCount { get; init; }
    public int ActiveModelSampleCount { get; init; }
    public string? ModelVersion { get; init; }
    public bool UsesActiveModelOnly { get; init; }
    public decimal GateThreshold { get; init; }
    public decimal PredictedMeanWin { get; init; }
    public decimal ActualWinRate { get; init; }
    public decimal CalibrationGap { get; init; }
    public decimal CalibrationGapAbs { get; init; }
    public decimal BrierScore { get; init; }
    public decimal RecommendedThresholdAvg { get; init; }
    public int PassCount { get; init; }
    public decimal? PassWinRate { get; init; }
    public int FailCount { get; init; }
    public decimal? FailWinRate { get; init; }
    public decimal? ThresholdLift { get; init; }
}

public sealed record MlFeatureDriftDiagnostics
{
    public required string Status { get; init; }
    public int TrainingSampleCount { get; init; }
    public int LiveSampleCount { get; init; }
    public int LiveWindowHours { get; init; }
    public decimal AveragePsi { get; init; }
    public decimal MaxPsi { get; init; }
    public decimal AverageMeanShiftSigma { get; init; }
    public IReadOnlyList<MlFeatureDriftItem> TopFeatures { get; init; } = [];
}

public sealed record MlFeatureDriftItem
{
    public required string Feature { get; init; }
    public decimal TrainingMean { get; init; }
    public decimal LiveMean { get; init; }
    public decimal Psi { get; init; }
    public decimal MeanShiftSigma { get; init; }
}
