using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Db;

public interface IMlPredictionRepository
{
    Task InsertAsync(MlPrediction prediction, CancellationToken ct = default);
    Task UpdateSignalIdAsync(Guid evaluationId, Guid signalId, CancellationToken ct = default);
    Task<MlPrediction?> GetLatestAsync(string symbol, string? timeframe = null, string scope = "all", CancellationToken ct = default);
    Task<IReadOnlyList<MlPrediction>> GetRecentAsync(string symbol, int hours, int limit, string? timeframe = null, string scope = "linked", CancellationToken ct = default);
    Task<MlPrediction?> GetBySignalIdAsync(Guid signalId, CancellationToken ct = default);
}
