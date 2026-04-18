namespace EthSignal.Infrastructure.Db;

/// <summary>
/// Persistence contract for the per-timeframe startup historical candle sync state.
/// Tracks empty-bootstrap and offline-gap-recovery progress so health/dashboard surfaces
/// can report what happened during the most recent startup sync without scanning logs.
/// </summary>
public interface ICandleSyncRepository
{
    Task UpsertAsync(CandleSyncStatusRow row, CancellationToken ct = default);
    Task<IReadOnlyList<CandleSyncStatusRow>> GetAllAsync(string symbol, CancellationToken ct = default);
    Task<CandleSyncStatusRow?> GetAsync(string symbol, string timeframe, CancellationToken ct = default);
}

/// <summary>
/// Single persisted record for one symbol/timeframe startup sync.
/// </summary>
public sealed record CandleSyncStatusRow(
    string Symbol,
    string Timeframe,
    string Status,
    string SyncMode,
    bool IsTableEmpty,
    DateTimeOffset? RequestedFromUtc,
    DateTimeOffset? RequestedToUtc,
    DateTimeOffset? LastExistingCandleUtc,
    DateTimeOffset? LastSyncedCandleUtc,
    long OfflineDurationSec,
    int ChunkSizeCandles,
    int ChunksTotal,
    int ChunksCompleted,
    DateTimeOffset? LastRunStartedAtUtc,
    DateTimeOffset? LastRunFinishedAtUtc,
    DateTimeOffset? LastSuccessAtUtc,
    string? LastError);
