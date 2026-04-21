using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Db;

public interface ITradeExecutionQueueRepository
{
    Task<long> InsertAsync(QueuedTradeExecution entry, CancellationToken ct = default);
    Task UpdateAsync(QueuedTradeExecution entry, CancellationToken ct = default);
    Task<QueuedTradeExecution?> GetActiveBySourceSignalAsync(Guid signalId, SignalExecutionSourceType sourceType, CancellationToken ct = default);
    Task<QueuedTradeExecution?> TryClaimNextQueuedAsync(DateTimeOffset claimedAtUtc, CancellationToken ct = default);
    Task<bool> HasQueuedAsync(CancellationToken ct = default);
    Task<IReadOnlyList<QueuedTradeExecution>> GetActiveEntriesAsync(int limit = 50, CancellationToken ct = default);
    Task<(int QueuedCount, int ProcessingCount, int CompletedCount, int FailedCount)> GetStatusCountsAsync(CancellationToken ct = default);
    Task<int> RequeueStaleProcessingAsync(DateTimeOffset staleBeforeUtc, CancellationToken ct = default);
}
