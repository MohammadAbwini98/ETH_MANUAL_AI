using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Db;

public interface IExecutedTradeRepository
{
    Task<long> InsertExecutedTradeAsync(ExecutedTrade trade, CancellationToken ct = default);
    Task UpdateExecutedTradeAsync(ExecutedTrade trade, CancellationToken ct = default);
    Task<ExecutedTrade?> GetExecutedTradeAsync(long executedTradeId, CancellationToken ct = default);
    Task<ExecutedTrade?> GetBySourceSignalAsync(Guid signalId, SignalExecutionSourceType sourceType, CancellationToken ct = default);
    Task<IReadOnlyDictionary<Guid, ExecutedTrade>> GetLatestBySourceSignalsAsync(IReadOnlyCollection<Guid> signalIds, SignalExecutionSourceType sourceType, CancellationToken ct = default);
    Task<IReadOnlyList<ExecutedTrade>> GetExecutedTradesAsync(ExecutedTradeQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<ExecutedTrade>> GetTradesForLifecycleReconciliationAsync(int limit = 200, CancellationToken ct = default);
    Task<int> GetExecutedTradeCountAsync(ExecutedTradeQuery query, CancellationToken ct = default);
    Task<ExecutedTradeStats> GetExecutionStatsAsync(CancellationToken ct = default);
    Task<ExecutedTradeStats> GetExecutionStatsAsync(ExecutedTradeQuery query, CancellationToken ct = default);
    Task<int> GetOpenExecutedTradeCountAsync(CancellationToken ct = default);
    Task<int> GetOpenExecutedTradeCountAsync(ExecutedTradeQuery query, CancellationToken ct = default);
    Task<int> GetActiveExecutedTradeCountAsync(ExecutedTradeQuery query, CancellationToken ct = default);
    Task<int> GetPendingOrSubmittedTradeCountAsync(ExecutedTradeQuery query, CancellationToken ct = default);
    Task InsertExecutionAttemptAsync(long? executedTradeId, Guid signalId, SignalExecutionSourceType sourceType, string attemptType, bool success, string? summary, string? errorDetails, string? brokerPayload, CancellationToken ct = default);
    Task InsertExecutionEventAsync(long? executedTradeId, Guid signalId, SignalExecutionSourceType sourceType, string eventType, string message, string? detailsJson, CancellationToken ct = default);
    Task<long> InsertAccountSnapshotAsync(AccountSnapshot snapshot, CancellationToken ct = default);
    Task<AccountSnapshot?> GetLatestAccountSnapshotAsync(CancellationToken ct = default);
    Task<AccountSnapshot?> GetLatestAccountSnapshotAsync(string? accountName, bool? isDemo, CancellationToken ct = default);
    Task InsertCloseTradeActionAsync(long executedTradeId, ForceCloseRequest request, ForceCloseResult result, CancellationToken ct = default);
}
