namespace EthSignal.Infrastructure.Db;

public sealed record MlTrainingRunRecord
{
    public long Id { get; init; }
    public required string ModelType { get; init; }
    public required string Trigger { get; init; }        // "auto", "manual", "drift"
    public DateTimeOffset DataStartUtc { get; init; }
    public DateTimeOffset DataEndUtc { get; init; }
    public int? SampleCount { get; init; }
    public int FoldCount { get; init; }
    public int EmbargoMars { get; init; }
    public required string Status { get; init; }         // "running", "success", "failed", "skipped"
    public long? ResultModelId { get; init; }
    public string? ErrorText { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? FinishedAtUtc { get; init; }
    public int? DurationSeconds { get; init; }
}

public interface IMlTrainingRunRepository
{
    Task<long> InsertAsync(MlTrainingRunRecord run, CancellationToken ct = default);
    Task UpdateAsync(long id, string status, int? sampleCount, long? resultModelId, string? errorText, int? durationSeconds, CancellationToken ct = default);
    Task<IReadOnlyList<MlTrainingRunRecord>> GetRecentAsync(int limit = 20, CancellationToken ct = default);
    Task<MlTrainingRunRecord?> GetLatestSuccessfulAsync(CancellationToken ct = default);
    Task<int> GetLabeledSampleCountAsync(CancellationToken ct = default);
    Task<(int wins, int losses)> GetWinLossCountsAsync(CancellationToken ct = default);
    Task<int> GetNewOutcomesSinceAsync(DateTimeOffset since, CancellationToken ct = default);
}
