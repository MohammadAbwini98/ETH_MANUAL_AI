using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Db;

public interface IReplayRepository
{
    Task<long> InsertRunAsync(ReplayRun run, CancellationToken ct = default);
    Task<ReplayRun?> GetRunAsync(long id, CancellationToken ct = default);
    Task UpdateRunStatusAsync(long id, RunStatus status, string? error = null, CancellationToken ct = default);
    Task UpdateRunProgressAsync(long id, int candlesRead, int signalsGenerated,
        int outcomesFinalized, DateTimeOffset? checkpoint, CancellationToken ct = default);
    Task UpdateRunFinishedAsync(long id, RunStatus status, int candlesRead,
        int signalsGenerated, int outcomesFinalized, CancellationToken ct = default);
}
