using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Apis;
using EthSignal.Infrastructure.Db;
using EthSignal.Infrastructure.Engine;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EthSignal.Tests.Engine;

public class HistoricalCandleSyncServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 4, 10, 10, 5, 30, TimeSpan.Zero);

    [Fact]
    public async Task PlanAsync_EmptyTable_UsesEmptyBootstrap()
    {
        var candleRepo = new Mock<ICandleRepository>();
        candleRepo
            .Setup(r => r.CountCandlesAsync(Timeframe.M15, "ETHUSD", It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var service = CreateService(candleRepo);

        var plan = await service.PlanAsync(Timeframe.M15, "ETHUSD", startupDays: 30, chunkCandles: 400, CancellationToken.None);

        var expectedTo = Timeframe.M15.Floor(FixedNow);
        var expectedFrom = Timeframe.M15.Floor(expectedTo.AddDays(-30));

        plan.Mode.Should().Be(TimeframeSyncMode.EmptyBootstrap);
        plan.IsTableEmpty.Should().BeTrue();
        plan.SyncFromUtc.Should().Be(expectedFrom);
        plan.SyncToUtc.Should().Be(expectedTo);
        plan.OfflineDuration.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task PlanAsync_StaleNonEmptyTable_UsesOfflineGapRecovery()
    {
        var latestClosed = new DateTimeOffset(2026, 4, 10, 9, 30, 0, TimeSpan.Zero);
        var earliestClosed = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var candleRepo = new Mock<ICandleRepository>();
        candleRepo
            .Setup(r => r.CountCandlesAsync(Timeframe.M15, "ETHUSD", It.IsAny<CancellationToken>()))
            .ReturnsAsync(120);
        candleRepo
            .Setup(r => r.GetEarliestClosedTimeAsync(Timeframe.M15, "ETHUSD", It.IsAny<CancellationToken>()))
            .ReturnsAsync(earliestClosed);
        candleRepo
            .Setup(r => r.GetLatestClosedTimeAsync(Timeframe.M15, "ETHUSD", It.IsAny<CancellationToken>()))
            .ReturnsAsync(latestClosed);

        var service = CreateService(candleRepo);

        var plan = await service.PlanAsync(Timeframe.M15, "ETHUSD", startupDays: 30, chunkCandles: 400, CancellationToken.None);

        plan.Mode.Should().Be(TimeframeSyncMode.OfflineGapRecovery);
        plan.IsTableEmpty.Should().BeFalse();
        plan.LastExistingClosed.Should().Be(latestClosed);
        plan.SyncFromUtc.Should().Be(latestClosed + Timeframe.M15.Duration);
        plan.SyncToUtc.Should().Be(Timeframe.M15.Floor(FixedNow));
        plan.OfflineDuration.Should().Be(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task PlanAsync_UpToDateTable_UsesNoop()
    {
        var currentOpenBoundary = Timeframe.M15.Floor(FixedNow);
        var latestClosed = currentOpenBoundary - Timeframe.M15.Duration;
        var earliestClosed = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var candleRepo = new Mock<ICandleRepository>();
        candleRepo
            .Setup(r => r.CountCandlesAsync(Timeframe.M15, "ETHUSD", It.IsAny<CancellationToken>()))
            .ReturnsAsync(120);
        candleRepo
            .Setup(r => r.GetEarliestClosedTimeAsync(Timeframe.M15, "ETHUSD", It.IsAny<CancellationToken>()))
            .ReturnsAsync(earliestClosed);
        candleRepo
            .Setup(r => r.GetLatestClosedTimeAsync(Timeframe.M15, "ETHUSD", It.IsAny<CancellationToken>()))
            .ReturnsAsync(latestClosed);

        var service = CreateService(candleRepo);

        var plan = await service.PlanAsync(Timeframe.M15, "ETHUSD", startupDays: 30, chunkCandles: 400, CancellationToken.None);

        plan.Mode.Should().Be(TimeframeSyncMode.Noop);
        plan.SyncFromUtc.Should().Be(currentOpenBoundary);
        plan.SyncToUtc.Should().Be(currentOpenBoundary);
        plan.ChunksTotal.Should().Be(0);
        plan.OfflineDuration.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task PlanAsync_PartialHistory_RebuildsBootstrapWindow()
    {
        var currentOpenBoundary = Timeframe.M1.Floor(FixedNow);
        var earliestClosed = currentOpenBoundary.AddDays(-2);
        var latestClosed = currentOpenBoundary - Timeframe.M1.Duration;
        var candleRepo = new Mock<ICandleRepository>();
        candleRepo
            .Setup(r => r.CountCandlesAsync(Timeframe.M1, "ETHUSD", It.IsAny<CancellationToken>()))
            .ReturnsAsync(2_880);
        candleRepo
            .Setup(r => r.GetEarliestClosedTimeAsync(Timeframe.M1, "ETHUSD", It.IsAny<CancellationToken>()))
            .ReturnsAsync(earliestClosed);
        candleRepo
            .Setup(r => r.GetLatestClosedTimeAsync(Timeframe.M1, "ETHUSD", It.IsAny<CancellationToken>()))
            .ReturnsAsync(latestClosed);

        var service = CreateService(candleRepo);

        var plan = await service.PlanAsync(Timeframe.M1, "ETHUSD", startupDays: 30, chunkCandles: 400, CancellationToken.None);

        plan.Mode.Should().Be(TimeframeSyncMode.EmptyBootstrap);
        plan.IsTableEmpty.Should().BeFalse();
        plan.SyncFromUtc.Should().Be(Timeframe.M1.Floor(currentOpenBoundary.AddDays(-30)));
        plan.SyncToUtc.Should().Be(currentOpenBoundary);
        plan.LastExistingClosed.Should().Be(latestClosed);
        plan.OfflineDuration.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void ComputeChunkCount_SplitsRangeByCandleCount()
    {
        var from = new DateTimeOffset(2026, 4, 10, 10, 0, 0, TimeSpan.Zero);
        var to = from.AddMinutes(10);

        var chunks = HistoricalCandleSyncService.ComputeChunkCount(from, to, Timeframe.M1, chunkCandles: 4);

        chunks.Should().Be(3);
    }

    [Fact]
    public async Task RunAsync_Retries429AndPersistsProgress()
    {
        var api = new Mock<ICapitalClient>();
        var candleRepo = new Mock<ICandleRepository>();
        var syncRepo = new Mock<ICandleSyncRepository>();
        var auditRepo = new Mock<IAuditRepository>();
        var persistedRows = new List<CandleSyncStatusRow>();
        var attempts = 0;

        candleRepo
            .Setup(r => r.CountCandlesAsync(It.IsAny<Timeframe>(), "ETHUSD", It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        candleRepo
            .Setup(r => r.GetLatestClosedTimeAsync(It.IsAny<Timeframe>(), "ETHUSD", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Timeframe tf, string _, CancellationToken _) => tf == Timeframe.M1
                ? new DateTimeOffset(2026, 4, 10, 10, 2, 0, TimeSpan.Zero)
                : tf.Floor(FixedNow) - tf.Duration);
        candleRepo
            .Setup(r => r.GetEarliestClosedTimeAsync(It.IsAny<Timeframe>(), "ETHUSD", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Timeframe tf, string _, CancellationToken _) => tf == Timeframe.M1
                ? new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)
                : tf.Floor(FixedNow).AddDays(-30));
        candleRepo
            .Setup(r => r.BulkUpsertAsync(It.IsAny<Timeframe>(), "ETHUSD", It.IsAny<IReadOnlyList<RichCandle>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Timeframe _, string _, IReadOnlyList<RichCandle> candles, CancellationToken _) => candles.Count);

        api
            .Setup(a => a.GetCandlesAsync("ETH-EPIC", Timeframe.M1.ApiResolution,
                It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, DateTimeOffset from, DateTimeOffset __, int ___, CancellationToken ____) =>
            {
                attempts++;
                if (attempts == 1)
                    throw new InvalidOperationException("429 rate limit");

                return [MakeCandle(from)];
            });

        syncRepo
            .Setup(r => r.UpsertAsync(It.IsAny<CandleSyncStatusRow>(), It.IsAny<CancellationToken>()))
            .Callback((CandleSyncStatusRow row, CancellationToken _) => persistedRows.Add(row))
            .Returns(Task.CompletedTask);

        auditRepo
            .Setup(r => r.InsertAuditAsync(It.IsAny<IngestionAuditEntry>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = CreateService(
            candleRepo,
            api: api,
            syncRepo: syncRepo,
            auditRepo: auditRepo,
            extraConfig: new Dictionary<string, string?>
            {
                ["CapitalApi:HistoricalSyncChunkCandles"] = "1",
                ["CapitalApi:HistoricalSyncChunkDelayMs"] = "0",
                ["CapitalApi:HistoricalSyncMaxRetries"] = "2",
                ["CapitalApi:HistoricalSyncRetryBaseDelayMs"] = "1"
            },
            delay: (_, _) => Task.CompletedTask);

        var summary = await service.RunAsync("ETHUSD", "ETH-EPIC", CancellationToken.None);

        summary.Status.Should().Be(TimeframeSyncStatus.Ready);
        summary.ReadyTimeframes.Should().Be(6);
        attempts.Should().Be(3);

        var m1Rows = persistedRows.Where(r => r.Timeframe == "1m").ToList();
        m1Rows.Should().Contain(r => r.Status == TimeframeSyncStatus.Running && r.ChunksCompleted == 1);
        m1Rows.Should().Contain(r => r.Status == TimeframeSyncStatus.Ready && r.ChunksCompleted == 2);
        m1Rows.Last().LastSyncedCandleUtc.Should().Be(new DateTimeOffset(2026, 4, 10, 10, 4, 0, TimeSpan.Zero));
    }

    [Fact]
    public void IsEnabled_DoesNotDependOnUiPriceOnly()
    {
        var service = CreateService(
            new Mock<ICandleRepository>(),
            extraConfig: new Dictionary<string, string?>
            {
                ["CapitalApi:HistoricalSyncEnabled"] = "true",
                ["HighFreqTicks:UiPriceOnly"] = "true"
            });

        service.IsEnabled.Should().BeTrue();
    }

    private static HistoricalCandleSyncService CreateService(
        Mock<ICandleRepository> candleRepo,
        Mock<ICapitalClient>? api = null,
        Mock<ICandleSyncRepository>? syncRepo = null,
        Mock<IAuditRepository>? auditRepo = null,
        IDictionary<string, string?>? extraConfig = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var configValues = new Dictionary<string, string?>
        {
            ["CapitalApi:HistoricalSyncEnabled"] = "true",
            ["CapitalApi:StartupHistoricalDays"] = "30",
            ["CapitalApi:HistoricalSyncChunkCandles"] = "400",
            ["CapitalApi:HistoricalSyncChunkDelayMs"] = "0",
            ["CapitalApi:HistoricalSyncMaxRetries"] = "6",
            ["CapitalApi:HistoricalSyncRetryBaseDelayMs"] = "1"
        };

        if (extraConfig != null)
        {
            foreach (var (key, value) in extraConfig)
                configValues[key] = value;
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        return new HistoricalCandleSyncService(
            api?.Object ?? Mock.Of<ICapitalClient>(),
            candleRepo.Object,
            syncRepo?.Object ?? Mock.Of<ICandleSyncRepository>(),
            auditRepo?.Object ?? Mock.Of<IAuditRepository>(),
            config,
            NullLogger<HistoricalCandleSyncService>.Instance,
            () => FixedNow,
            delay);
    }

    private static RichCandle MakeCandle(DateTimeOffset openTime, decimal basePrice = 2000m)
    {
        return new RichCandle
        {
            OpenTime = openTime,
            BidOpen = basePrice,
            BidHigh = basePrice + 5,
            BidLow = basePrice - 5,
            BidClose = basePrice + 2,
            AskOpen = basePrice + 1,
            AskHigh = basePrice + 6,
            AskLow = basePrice - 4,
            AskClose = basePrice + 3,
            Volume = 10,
            BuyerPct = 50,
            SellerPct = 50,
            SourceTimestampUtc = openTime,
            ReceivedTimestampUtc = openTime.AddSeconds(1),
            IsClosed = true
        };
    }
}
