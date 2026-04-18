using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Db;

public interface IRegimeRepository
{
    Task UpsertAsync(RegimeResult result, CancellationToken ct = default);
    Task<RegimeResult?> GetLatestAsync(string symbol, CancellationToken ct = default);
    Task<RegimeResult?> GetLatestBeforeAsync(string symbol, DateTimeOffset before, CancellationToken ct = default);
    Task<IReadOnlyList<RegimeResult>> GetHistoryAsync(string symbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}
