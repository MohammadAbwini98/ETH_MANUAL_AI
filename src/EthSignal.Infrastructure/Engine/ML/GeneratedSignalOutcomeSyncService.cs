using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using Microsoft.Extensions.Logging;

namespace EthSignal.Infrastructure.Engine.ML;

public sealed record GeneratedSignalOutcomeSyncResult
{
    public int TotalSynced { get; init; }
    public int LabeledWins { get; init; }
    public int LabeledLosses { get; init; }
}

public interface IGeneratedSignalOutcomeSyncService
{
    Task<GeneratedSignalOutcomeSyncResult> SyncAsync(
        string symbol,
        CancellationToken ct = default);
}

public sealed class GeneratedSignalOutcomeSyncService : IGeneratedSignalOutcomeSyncService
{
    private readonly IGeneratedSignalHistoryService _generatedHistory;
    private readonly IGeneratedSignalOutcomeRepository _repository;
    private readonly ILogger<GeneratedSignalOutcomeSyncService> _logger;

    public GeneratedSignalOutcomeSyncService(
        IGeneratedSignalHistoryService generatedHistory,
        IGeneratedSignalOutcomeRepository repository,
        ILogger<GeneratedSignalOutcomeSyncService> logger)
    {
        _generatedHistory = generatedHistory;
        _repository = repository;
        _logger = logger;
    }

    public async Task<GeneratedSignalOutcomeSyncResult> SyncAsync(
        string symbol,
        CancellationToken ct = default)
    {
        var firstPage = await _generatedHistory.GetHistoryAsync(symbol, 1, 0, null, ct);
        if (firstPage.Total <= 0)
            return new GeneratedSignalOutcomeSyncResult();

        var fullPage = firstPage.Total <= firstPage.PageSize
            ? firstPage
            : await _generatedHistory.GetHistoryAsync(symbol, firstPage.Total, 0, null, ct);

        var syncable = fullPage.Signals
            .Where(item => item.Signal.EvaluationId != Guid.Empty)
            .GroupBy(item => item.Signal.SignalId)
            .Select(g => g.First())
            .ToArray();

        await _repository.UpsertManyAsync(syncable, ct);

        var wins = syncable.Count(item => item.Outcome.OutcomeLabel == OutcomeLabel.WIN);
        var losses = syncable.Count(item => item.Outcome.OutcomeLabel == OutcomeLabel.LOSS);

        _logger.LogInformation(
            "[MLTrainer] Synced generated outcomes | Symbol={Symbol} | Total={Total} | Wins={Wins} | Losses={Losses}",
            symbol,
            syncable.Length,
            wins,
            losses);

        return new GeneratedSignalOutcomeSyncResult
        {
            TotalSynced = syncable.Length,
            LabeledWins = wins,
            LabeledLosses = losses
        };
    }
}
