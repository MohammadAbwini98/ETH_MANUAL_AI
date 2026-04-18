using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Db;

public interface IParameterRepository
{
    Task<StrategyParameterSet?> GetActiveAsync(string strategyVersion, CancellationToken ct = default);
    Task<StrategyParameterSet?> GetByIdAsync(long id, CancellationToken ct = default);
    Task<long> InsertAsync(StrategyParameterSet set, CancellationToken ct = default);
    Task ActivateAsync(long id, long? previousId, string? activatedBy, string? reason, CancellationToken ct = default);
    Task UpdateStatusAsync(long id, ParameterSetStatus status, CancellationToken ct = default);
    Task<IReadOnlyList<StrategyParameterSet>> GetCandidatesAsync(string strategyVersion, CancellationToken ct = default);
}
