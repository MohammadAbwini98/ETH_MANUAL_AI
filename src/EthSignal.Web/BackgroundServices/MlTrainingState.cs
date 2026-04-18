namespace EthSignal.Web.BackgroundServices;

/// <summary>
/// Thread-safe singleton that holds the current ML training status,
/// readable by API endpoints and dashboard without DB queries.
/// </summary>
public sealed class MlTrainingState
{
    private readonly object _lock = new();

    public bool IsRunning { get; private set; }
    public long? CurrentRunId { get; private set; }
    public string? CurrentTrigger { get; private set; }
    public DateTimeOffset? RunStartedAt { get; private set; }
    public string? LastStatus { get; private set; }       // "success", "failed", "skipped", null
    public TimeSpan? LastDuration { get; private set; }
    public long? LastResultModelId { get; private set; }
    public DateTimeOffset? LastCompletedAt { get; private set; }

    // Readiness snapshot
    public int LabeledSamples { get; private set; }
    public int Wins { get; private set; }
    public int Losses { get; private set; }

    public void UpdateReadiness(int total, int wins, int losses)
    {
        lock (_lock) { LabeledSamples = total; Wins = wins; Losses = losses; }
    }

    public void SetRunning(long runId, string trigger)
    {
        lock (_lock)
        {
            IsRunning = true;
            CurrentRunId = runId;
            CurrentTrigger = trigger;
            RunStartedAt = DateTimeOffset.UtcNow;
        }
    }

    public void SetIdle(string status, TimeSpan duration, long? modelId)
    {
        lock (_lock)
        {
            IsRunning = false;
            LastStatus = status;
            LastDuration = duration;
            LastResultModelId = modelId;
            LastCompletedAt = DateTimeOffset.UtcNow;
            CurrentRunId = null;
            CurrentTrigger = null;
            RunStartedAt = null;
        }
    }
}
