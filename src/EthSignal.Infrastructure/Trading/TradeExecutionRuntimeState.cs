using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Trading;

public sealed class TradeExecutionRuntimeState
{
    private readonly object _lock = new();

    public DateTimeOffset? LastSyncUtc { get; private set; }
    public DateTimeOffset? LastAccountResolutionUtc { get; private set; }
    public string? LatestBrokerError { get; private set; }
    public string? LatestOrderNote { get; private set; }
    public string? ActiveAccountId { get; private set; }
    public string? ActiveAccountName { get; private set; }
    public bool? ActiveAccountIsDemo { get; private set; }
    public string? AccountSelectionSource { get; private set; }
    public string? LatestExecutionAccountId { get; private set; }
    public string? LatestExecutionAccountName { get; private set; }
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

    public void RecordAccountContext(CapitalAccountInfo info)
    {
        lock (_lock)
        {
            ActiveAccountId = info.AccountId;
            ActiveAccountName = info.AccountName;
            ActiveAccountIsDemo = info.IsDemo;
            AccountSelectionSource = info.ResolutionSource;
            LastAccountResolutionUtc = info.ResolvedAtUtc ?? DateTimeOffset.UtcNow;
            LastSyncUtc = DateTimeOffset.UtcNow;
        }
    }

    public void RecordExecutionAccount(AccountSnapshot snapshot)
    {
        lock (_lock)
        {
            LatestExecutionAccountId = snapshot.AccountId;
            LatestExecutionAccountName = snapshot.AccountName;
            LastSyncUtc = DateTimeOffset.UtcNow;
        }
    }
}
