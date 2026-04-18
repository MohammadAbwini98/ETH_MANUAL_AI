using EthSignal.Infrastructure.Engine;

namespace EthSignal.Infrastructure.Db;

public interface IGeneratedSignalOutcomeRepository
{
    Task UpsertManyAsync(
        IReadOnlyList<GeneratedSignalWithOutcome> items,
        CancellationToken ct = default);
}
