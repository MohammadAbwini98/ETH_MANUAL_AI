using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using EthSignal.Infrastructure.Engine;
using EthSignal.Infrastructure.Engine.ML;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EthSignal.Tests.Engine.ML;

public sealed class GeneratedSignalOutcomeSyncServiceTests
{
    [Fact]
    public async Task SyncAsync_Upserts_All_Generated_Outcomes_And_Returns_Labeled_Counts()
    {
        var history = new Mock<IGeneratedSignalHistoryService>();
        var repo = new Mock<IGeneratedSignalOutcomeRepository>();

        history.Setup(h => h.GetHistoryAsync("ETHUSD", 1, 0, It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GeneratedSignalHistoryPage
            {
                Signals =
                [
                    MakeItem("1m", OutcomeLabel.WIN),
                ],
                Stats = new PerformanceStats(),
                Total = 3,
                Page = 1,
                PageSize = 1
            });

        history.Setup(h => h.GetHistoryAsync("ETHUSD", 3, 0, It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GeneratedSignalHistoryPage
            {
                Signals =
                [
                    MakeItem("1m", OutcomeLabel.WIN),
                    MakeItem("5m", OutcomeLabel.LOSS),
                    MakeItem("15m", OutcomeLabel.EXPIRED),
                ],
                Stats = new PerformanceStats(),
                Total = 3,
                Page = 1,
                PageSize = 3
            });

        IReadOnlyList<GeneratedSignalWithOutcome>? saved = null;
        repo.Setup(r => r.UpsertManyAsync(It.IsAny<IReadOnlyList<GeneratedSignalWithOutcome>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<GeneratedSignalWithOutcome>, CancellationToken>((items, _) => saved = items)
            .Returns(Task.CompletedTask);

        var service = new GeneratedSignalOutcomeSyncService(
            history.Object,
            repo.Object,
            NullLogger<GeneratedSignalOutcomeSyncService>.Instance);

        var result = await service.SyncAsync("ETHUSD", CancellationToken.None);

        result.TotalSynced.Should().Be(3);
        result.LabeledWins.Should().Be(1);
        result.LabeledLosses.Should().Be(1);
        saved.Should().NotBeNull();
        saved!.Count.Should().Be(3);
    }

    private static GeneratedSignalWithOutcome MakeItem(string timeframe, OutcomeLabel outcomeLabel)
    {
        var signalId = Guid.NewGuid();
        return new GeneratedSignalWithOutcome
        {
            Signal = new GeneratedSignalRecommendation
            {
                SignalId = signalId,
                EvaluationId = Guid.NewGuid(),
                Symbol = "ETHUSD",
                Timeframe = timeframe,
                SignalTimeUtc = new DateTimeOffset(2026, 4, 17, 12, 0, 0, TimeSpan.Zero),
                DecisionTimeUtc = new DateTimeOffset(2026, 4, 17, 11, 59, 0, TimeSpan.Zero),
                BarTimeUtc = new DateTimeOffset(2026, 4, 17, 11, 55, 0, TimeSpan.Zero),
                Direction = SignalDirection.SELL,
                LifecycleState = SignalLifecycleState.CANDIDATE_CREATED,
                Origin = DecisionOrigin.PARTIAL_RUNNING,
                SourceMode = SourceMode.LIVE,
                Regime = Regime.BEARISH,
                StrategyVersion = "v3.0",
                Reasons = ["generated"],
                EntryPrice = 2400m,
                TpPrice = 2390m,
                SlPrice = 2405m,
                RiskPercent = 0.5m,
                RiskUsd = 10m,
                ConfidenceScore = 70,
                ExpiryBars = 4,
                ExpiryTimeUtc = new DateTimeOffset(2026, 4, 17, 12, 20, 0, TimeSpan.Zero),
                ExitModel = "STRUCTURE_FULL",
                ExitExplanation = "generated test"
            },
            Outcome = new SignalOutcome
            {
                SignalId = signalId,
                OutcomeLabel = outcomeLabel,
                EvaluatedAtUtc = new DateTimeOffset(2026, 4, 17, 13, 0, 0, TimeSpan.Zero),
                BarsObserved = 4,
                TpHit = outcomeLabel == OutcomeLabel.WIN,
                SlHit = outcomeLabel == OutcomeLabel.LOSS,
                PnlR = outcomeLabel == OutcomeLabel.WIN ? 1m : outcomeLabel == OutcomeLabel.LOSS ? -1m : 0m,
                MfePrice = 2410m,
                MaePrice = 2395m,
                MfeR = 1m,
                MaeR = -1m,
                ClosedAtUtc = new DateTimeOffset(2026, 4, 17, 12, 20, 0, TimeSpan.Zero)
            }
        };
    }
}
