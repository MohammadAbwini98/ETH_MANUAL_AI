using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Db;

public interface ITickSnapshotRepository
{
    Task InsertAsync(
        string symbol,
        string epic,
        SpotPrice spot,
    string providerKind,
        CancellationToken ct = default);
}
