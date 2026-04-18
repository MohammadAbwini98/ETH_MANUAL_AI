namespace EthSignal.Infrastructure.Engine;

/// <summary>
/// Process-wide cache of the most recent startup candle sync summary.
/// Populated by <see cref="HistoricalCandleSyncService"/> via DataIngestionService
/// and consumed by /health and /api/admin/candle-sync/status so the dashboard can
/// surface a real-time view without an extra DB hit on every request.
/// </summary>
public sealed class CandleSyncState
{
    private readonly object _gate = new();
    private StartupCandleSyncSummary? _latest;

    public StartupCandleSyncSummary? Latest
    {
        get { lock (_gate) return _latest; }
    }

    public void Set(StartupCandleSyncSummary summary)
    {
        lock (_gate) _latest = summary;
    }
}
