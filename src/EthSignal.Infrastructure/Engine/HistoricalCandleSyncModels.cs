using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Engine;

/// <summary>
/// One of three startup modes a timeframe can take. Selection happens
/// per timeframe based on the current state of its candle table.
/// </summary>
public static class TimeframeSyncMode
{
    public const string EmptyBootstrap = "EMPTY_BOOTSTRAP";
    public const string OfflineGapRecovery = "OFFLINE_GAP_RECOVERY";
    public const string Noop = "NOOP";
}

/// <summary>
/// Lifecycle status for a single timeframe sync.
/// </summary>
public static class TimeframeSyncStatus
{
    public const string Pending = "PENDING";
    public const string Running = "RUNNING";
    public const string Ready = "READY";
    public const string Failed = "FAILED";
}

/// <summary>
/// Resolved per-timeframe plan for a single startup sync run.
/// </summary>
public sealed record TimeframeSyncPlan
{
    public required Timeframe Tf { get; init; }
    public required string Mode { get; init; }
    public required bool IsTableEmpty { get; init; }
    public DateTimeOffset? LastExistingClosed { get; init; }
    public DateTimeOffset SyncFromUtc { get; init; }
    public DateTimeOffset SyncToUtc { get; init; }
    public int ChunkSizeCandles { get; init; }
    public int ChunksTotal { get; init; }
    public TimeSpan OfflineDuration { get; init; }
}

/// <summary>
/// Outcome of running one timeframe sync.
/// </summary>
public sealed record TimeframeSyncResult
{
    public required Timeframe Tf { get; init; }
    public required string Mode { get; init; }
    public required string Status { get; init; }
    public int ChunksCompleted { get; init; }
    public int ChunksTotal { get; init; }
    public int CandlesFetched { get; init; }
    public int CandlesUpserted { get; init; }
    public DateTimeOffset? LastSyncedCandleUtc { get; init; }
    public TimeSpan Elapsed { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Aggregated startup summary across all timeframes.
/// Surfaced in /health and /api/admin/candle-sync/status.
/// </summary>
public sealed record StartupCandleSyncSummary
{
    public required string Symbol { get; init; }
    public required string Status { get; init; }
    public int TotalTimeframes { get; init; }
    public int ReadyTimeframes { get; init; }
    public int FailedTimeframes { get; init; }
    public int RunningTimeframes { get; init; }
    public int NoopTimeframes { get; init; }
    public TimeSpan Elapsed { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? FinishedAtUtc { get; init; }
    public IReadOnlyList<TimeframeSyncResult> Timeframes { get; init; } = [];
}
