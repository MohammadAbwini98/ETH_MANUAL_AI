using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Db;

public interface IOptimizerRepository
{
    Task<long> InsertRunAsync(OptimizerRun run, CancellationToken ct = default);
    Task UpdateRunStatusAsync(long id, RunStatus status, string? error = null, CancellationToken ct = default);
    Task UpdateRunFinishedAsync(long id, RunStatus status, long? bestCandidateId,
        decimal? bestScore, int candidateCount, string? summaryJson, CancellationToken ct = default);
    Task<long> InsertCandidateAsync(OptimizerCandidate candidate, CancellationToken ct = default);
    Task InsertCandidateFoldAsync(OptimizerCandidateFold fold, CancellationToken ct = default);
    Task<OptimizerRun?> GetRunAsync(long id, CancellationToken ct = default);
}
