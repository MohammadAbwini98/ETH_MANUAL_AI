using EthSignal.Infrastructure.Engine;

namespace EthSignal.Infrastructure.Db;

public interface IBlockedSignalOutcomeRepository
{
    Task UpsertManyAsync(
        IReadOnlyList<BlockedSignalWithOutcome> items,
        CancellationToken ct = default);
}
