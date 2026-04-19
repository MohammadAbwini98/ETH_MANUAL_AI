namespace EthSignal.Infrastructure.Trading;

public sealed class TradeExecutionRuntimeState
{
    private readonly object _lock = new();

    public DateTimeOffset? LastSyncUtc { get; private set; }
    public string? LatestBrokerError { get; private set; }
    public string? LatestOrderNote { get; private set; }
    public bool SessionReady { get; private set; }

    public void RecordSync(bool sessionReady, string? note = null)
    {
        lock (_lock)
        {
            SessionReady = sessionReady;
            LastSyncUtc = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(note))
                LatestOrderNote = note;
        }
    }

    public void RecordBrokerError(string error)
    {
        lock (_lock)
        {
            LatestBrokerError = error;
            LastSyncUtc = DateTimeOffset.UtcNow;
        }
    }

    public void RecordOrderNote(string note)
    {
        lock (_lock)
        {
            LatestOrderNote = note;
            LastSyncUtc = DateTimeOffset.UtcNow;
        }
    }
}
