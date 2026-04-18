using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using Microsoft.Extensions.Logging;

namespace EthSignal.Infrastructure.Engine.ML;

public sealed record BlockedSignalOutcomeSyncResult
{
    public int TotalSynced { get; init; }
    public int LabeledWins { get; init; }
    public int LabeledLosses { get; init; }
}

public interface IBlockedSignalOutcomeSyncService
{
    Task<BlockedSignalOutcomeSyncResult> SyncAsync(
        string symbol,
        CancellationToken ct = default);
}

public sealed class BlockedSignalOutcomeSyncService : IBlockedSignalOutcomeSyncService
{
    private readonly IBlockedSignalHistoryService _blockedHistory;
    private readonly IBlockedSignalOutcomeRepository _repository;
    private readonly ILogger<BlockedSignalOutcomeSyncService> _logger;

    public BlockedSignalOutcomeSyncService(
        IBlockedSignalHistoryService blockedHistory,
        IBlockedSignalOutcomeRepository repository,
        ILogger<BlockedSignalOutcomeSyncService> logger)
    {
        _blockedHistory = blockedHistory;
        _repository = repository;
        _logger = logger;
    }

    public async Task<BlockedSignalOutcomeSyncResult> SyncAsync(
        string symbol,
        CancellationToken ct = default)
    {
        var firstPage = await _blockedHistory.GetHistoryAsync(symbol, 1, 0, ct);
        if (firstPage.Total <= 0)
            return new BlockedSignalOutcomeSyncResult();

        var fullPage = firstPage.Total <= firstPage.PageSize
            ? firstPage
            : await _blockedHistory.GetHistoryAsync(symbol, firstPage.Total, 0, ct);

        var syncable = fullPage.Signals
            .Where(item => item.Signal.EvaluationId != Guid.Empty)
            .GroupBy(item => item.Signal.SignalId)
            .Select(g => g.First())
            .ToArray();

        await _repository.UpsertManyAsync(syncable, ct);

        var wins = syncable.Count(item => item.Outcome.OutcomeLabel == OutcomeLabel.WIN);
        var losses = syncable.Count(item => item.Outcome.OutcomeLabel == OutcomeLabel.LOSS);

        _logger.LogInformation(
            "[MLTrainer] Synced blocked outcomes | Symbol={Symbol} | Total={Total} | Wins={Wins} | Losses={Losses}",
            symbol,
            syncable.Length,
            wins,
            losses);

        return new BlockedSignalOutcomeSyncResult
        {
            TotalSynced = syncable.Length,
            LabeledWins = wins,
            LabeledLosses = losses
        };
    }
}
