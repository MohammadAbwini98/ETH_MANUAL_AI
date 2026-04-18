namespace EthSignal.Infrastructure.Db;

public interface IMlDataDiagnosticsRepository
{
    Task<IReadOnlyList<MlFeatureVersionStats>> GetFeatureVersionStatsAsync(
        string symbol,
        string timeframe,
        CancellationToken ct = default);

    Task<MlOutcomeQualityRaw> GetOutcomeQualityAsync(
        string symbol,
        string timeframe,
        string featureVersion,
        int staleUnlinkedHours,
        CancellationToken ct = default);

    Task<IReadOnlyList<MlPredictionOutcomeSample>> GetPredictionOutcomeSamplesAsync(
        string symbol,
        string timeframe,
        string? modelVersion,
        int days,
        int limit,
        CancellationToken ct = default);

    Task<IReadOnlyList<MlFeatureSnapshotSample>> GetLabeledFeatureSamplesAsync(
        string symbol,
        string timeframe,
        string featureVersion,
        int limit,
        CancellationToken ct = default);

    Task<IReadOnlyList<MlFeatureSnapshotSample>> GetRecentFeatureSamplesAsync(
        string symbol,
        string timeframe,
        string featureVersion,
        int hours,
        int limit,
        CancellationToken ct = default);
}

public sealed record MlOutcomeQualityRaw(
    int TotalOutcomes,
    int Wins,
    int Losses,
    int Pending,
    int Expired,
    int Ambiguous,
    int InconsistentPnlLabels,
    int ConflictingTpSlHits,
    int ClosedTimestampMissing,
    int TotalFeatureSnapshots,
    int LinkedFeatureSnapshots,
    int LabeledFeatureSnapshots,
    int PendingLinkSnapshots,
    int StalePendingLinkSnapshots,
    int ExpectedNoSignalSnapshots,
    int MlFilteredSnapshots,
    int OperationallyBlockedSnapshots,
    int TrainableFeatureSnapshots = 0);

public sealed record MlPredictionOutcomeSample(
    DateTimeOffset PredictionTimeUtc,
    decimal CalibratedWinProbability,
    int RecommendedThreshold,
    string ModelVersion,
    bool ActualWin);

public sealed record MlFeatureSnapshotSample(
    Guid EvaluationId,
    DateTimeOffset TimestampUtc,
    IReadOnlyDictionary<string, double> Features);

public sealed record MlFeatureVersionStats(
    string FeatureVersion,
    int TotalSnapshots,
    int LabeledFeatureSnapshots,
    int TrainableFeatureSnapshots,
    DateTimeOffset? LatestCreatedAtUtc);
