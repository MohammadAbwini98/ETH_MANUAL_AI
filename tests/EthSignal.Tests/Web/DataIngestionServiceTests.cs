using System.Runtime.CompilerServices;
using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Apis;
using EthSignal.Infrastructure.Db;
using EthSignal.Infrastructure.Engine;
using EthSignal.Infrastructure.Notifications;
using EthSignal.Web.BackgroundServices;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EthSignal.Tests.Web;

public class DataIngestionServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 4, 10, 10, 5, 30, TimeSpan.Zero);

    [Fact]
    public async Task UiPriceOnly_DoesNotDisableHistoricalSync()
    {
        var api = new Mock<ICapitalClient>();
        var migrator = new Mock<IDbMigrator>();
        var candleRepo = new Mock<ICandleRepository>();
        var syncRepo = new Mock<ICandleSyncRepository>();
        var auditRepo = new Mock<IAuditRepository>();
        var paramProvider = new Mock<IParameterProvider>();
        var telegram = new Mock<ITelegramNotifier>();

        api.Setup(a => a.AuthenticateAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        api.Setup(a => a.GetCandlesAsync("ETH-EPIC", Timeframe.M1.ApiResolution,
                It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("429 rate limit"));

        migrator.Setup(m => m.MigrateAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        candleRepo.Setup(r => r.CountCandlesAsync(It.IsAny<Timeframe>(), "ETHUSD", It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        candleRepo.Setup(r => r.GetLatestClosedTimeAsync(It.IsAny<Timeframe>(), "ETHUSD", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Timeframe tf, string _, CancellationToken _) => tf == Timeframe.M1
                ? new DateTimeOffset(2026, 4, 10, 10, 2, 0, TimeSpan.Zero)
                : tf.Floor(FixedNow) - tf.Duration);
        candleRepo.Setup(r => r.GetEarliestClosedTimeAsync(It.IsAny<Timeframe>(), "ETHUSD", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Timeframe tf, string _, CancellationToken _) => tf == Timeframe.M1
                ? new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)
                : tf.Floor(FixedNow).AddDays(-30));

        syncRepo.Setup(r => r.UpsertAsync(It.IsAny<CandleSyncStatusRow>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        auditRepo.Setup(r => r.InsertAuditAsync(It.IsAny<IngestionAuditEntry>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        paramProvider.Setup(p => p.GetActive())
            .Returns(StrategyParameters.Default with { BackfillReplaySignals = false });
        telegram.Setup(t => t.SendAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CapitalApi:Symbol"] = "ETHUSD",
                ["CapitalApi:Epic"] = "ETH-EPIC",
                ["CapitalApi:BackfillDays"] = "7",
                ["CapitalApi:HistoricalSyncEnabled"] = "true",
                ["CapitalApi:HistoricalSyncChunkCandles"] = "1",
                ["CapitalApi:HistoricalSyncChunkDelayMs"] = "0",
                ["CapitalApi:HistoricalSyncMaxRetries"] = "1",
                ["CapitalApi:HistoricalSyncRetryBaseDelayMs"] = "1",
                ["CapitalApi:LegacyBackfillEnabled"] = "false",
                ["HighFreqTicks:UiPriceOnly"] = "true"
            })
            .Build();

        var historicalSync = new HistoricalCandleSyncService(
            api.Object,
            candleRepo.Object,
            syncRepo.Object,
            auditRepo.Object,
            config,
            NullLogger<HistoricalCandleSyncService>.Instance,
            () => FixedNow,
            (_, _) => Task.CompletedTask);

        var backfillService = new BackfillService(
            api.Object,
            candleRepo.Object,
            auditRepo.Object,
            Mock.Of<IIndicatorRepository>(),
            Mock.Of<IRegimeRepository>(),
            paramProvider.Object,
            NullLogger<BackfillService>.Instance);

        var replayService = new HistoricalReplayService(
            candleRepo.Object,
            Mock.Of<ISignalRepository>(),
            Mock.Of<IReplayRepository>(),
            NullLogger<HistoricalReplayService>.Instance);

        var state = new CandleSyncState();
        var service = new DataIngestionService(
            api.Object,
            migrator.Object,
            backfillService,
            historicalSync,
            state,
            CreateUnusedLiveTickProcessor(),
            replayService,
            Mock.Of<IDecisionAuditRepository>(),
            paramProvider.Object,
            config,
            NullLogger<DataIngestionService>.Instance,
            telegram.Object);

        var act = async () => await service.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        api.Verify(a => a.AuthenticateAsync(It.IsAny<CancellationToken>()), Times.Once);
        syncRepo.Verify(r => r.UpsertAsync(It.IsAny<CandleSyncStatusRow>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        state.Latest.Should().NotBeNull();
        state.Latest!.FailedTimeframes.Should().BeGreaterThan(0);
    }

    private static LiveTickProcessor CreateUnusedLiveTickProcessor()
    {
        return (LiveTickProcessor)RuntimeHelpers.GetUninitializedObject(typeof(LiveTickProcessor));
    }
}
