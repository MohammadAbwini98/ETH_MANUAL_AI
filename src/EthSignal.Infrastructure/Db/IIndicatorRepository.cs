using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Db;

public interface IIndicatorRepository
{
    Task BulkUpsertAsync(IReadOnlyList<IndicatorSnapshot> snapshots, CancellationToken ct = default);

    Task<IReadOnlyList<IndicatorSnapshot>> GetSnapshotsAsync(
        string symbol, string timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);

    Task<IndicatorSnapshot?> GetLatestAsync(string symbol, string timeframe, CancellationToken ct = default);
}
