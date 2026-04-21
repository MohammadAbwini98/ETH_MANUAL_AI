using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using EthSignal.Infrastructure.Engine;
using EthSignal.Infrastructure.Trading;
using EthSignal.Web.BackgroundServices;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EthSignal.Tests.Web;

public sealed class TradeAutoExecutionServiceTests
{
    [Fact]
    public async Task RunOnceAsync_WhenRecommendedExecutorIsOff_SkipsRecommendedSignals()
    {
        var signalRepo = new Mock<ISignalRepository>();
        signalRepo.Setup(r => r.GetSignalHistoryAsync("ETHUSD", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                CreateRecommendedSignal()
            ]);

        var queueService = new Mock<ITradeExecutionQueueService>();
        var sut = CreateSut(
            signalRepo: signalRepo,
            queueService: queueService,
            portalOverrides: new PortalOverrides { RecommendedSignalExecutionEnabled = false },
            allowedSourceTypes: [SignalExecutionSourceType.Recommended]);

        await sut.RunOnceAsync(CancellationToken.None);

        queueService.Verify(s => s.EnqueueAsync(It.IsAny<TradeExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunOnceAsync_WhenRecommendedExecutorIsOn_ExecutesRecommendedSignals()
    {
        var signalRepo = new Mock<ISignalRepository>();
        signalRepo.Setup(r => r.GetSignalHistoryAsync("ETHUSD", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                CreateRecommendedSignal()
            ]);

        var queueService = new Mock<ITradeExecutionQueueService>();
        queueService.Setup(s => s.EnqueueAsync(It.IsAny<TradeExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TradeExecutionQueueResult
            {
                Accepted = true,
                QueueEntryId = 1,
                Status = TradeExecutionQueueStatus.Queued.ToString(),
                Message = "ok"
            });

        var sut = CreateSut(
            signalRepo: signalRepo,
            queueService: queueService,
            portalOverrides: new PortalOverrides { RecommendedSignalExecutionEnabled = true },
            allowedSourceTypes: [SignalExecutionSourceType.Recommended]);

        await sut.RunOnceAsync(CancellationToken.None);

        queueService.Verify(s => s.EnqueueAsync(
            It.Is<TradeExecutionRequest>(r => r.Candidate.SourceType == SignalExecutionSourceType.Recommended),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunOnceAsync_WhenRecommendedExecutorIsOff_StillExecutesGeneratedAndBlocked()
    {
        var signalRepo = new Mock<ISignalRepository>();
        signalRepo.Setup(r => r.GetSignalHistoryAsync("ETHUSD", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                CreateRecommendedSignal()
            ]);

        var generatedHistory = new Mock<IGeneratedSignalHistoryService>();
        generatedHistory.Setup(s => s.GetHistoryAsync("ETHUSD", 100, 0, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GeneratedSignalHistoryPage
            {
                Signals =
                [
                    new GeneratedSignalWithOutcome
                    {
                        Signal = CreateGeneratedSignal(),
                        Outcome = CreateOutcome()
                    }
                ],
                Stats = new PerformanceStats(),
                Total = 1,
                Page = 1,
                PageSize = 100
            });

        var blockedHistory = new Mock<IBlockedSignalHistoryService>();
        blockedHistory.Setup(s => s.GetHistoryAsync("ETHUSD", 100, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BlockedSignalHistoryPage
            {
                Signals =
                [
                    new BlockedSignalWithOutcome
                    {
                        Signal = CreateBlockedSignal(),
                        Outcome = CreateOutcome()
                    }
                ],
                Stats = new PerformanceStats(),
                Total = 1,
                Page = 1,
                PageSize = 100
            });

        var queueService = new Mock<ITradeExecutionQueueService>();
        queueService.Setup(s => s.EnqueueAsync(It.IsAny<TradeExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TradeExecutionQueueResult
            {
                Accepted = true,
                QueueEntryId = 1,
                Status = TradeExecutionQueueStatus.Queued.ToString(),
                Message = "ok"
            });

        var sut = CreateSut(
            signalRepo: signalRepo,
            generatedHistory: generatedHistory,
            blockedHistory: blockedHistory,
            queueService: queueService,
            portalOverrides: new PortalOverrides { RecommendedSignalExecutionEnabled = false },
            allowedSourceTypes:
            [
                SignalExecutionSourceType.Recommended,
                SignalExecutionSourceType.Generated,
                SignalExecutionSourceType.Blocked
            ]);

        await sut.RunOnceAsync(CancellationToken.None);

        queueService.Verify(s => s.EnqueueAsync(
            It.Is<TradeExecutionRequest>(r => r.Candidate.SourceType == SignalExecutionSourceType.Recommended),
            It.IsAny<CancellationToken>()), Times.Never);
        queueService.Verify(s => s.EnqueueAsync(
            It.Is<TradeExecutionRequest>(r => r.Candidate.SourceType == SignalExecutionSourceType.Generated),
            It.IsAny<CancellationToken>()), Times.Once);
        queueService.Verify(s => s.EnqueueAsync(
            It.Is<TradeExecutionRequest>(r => r.Candidate.SourceType == SignalExecutionSourceType.Blocked),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunOnceAsync_Skips_Closed_Recommended_Signals()
    {
        var signalRepo = new Mock<ISignalRepository>();
        signalRepo.Setup(r => r.GetSignalHistoryAsync("ETHUSD", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                CreateRecommendedSignal() with { Status = SignalStatus.CLOSED }
            ]);

        var queueService = new Mock<ITradeExecutionQueueService>();
        var sut = CreateSut(
            signalRepo: signalRepo,
            queueService: queueService,
            portalOverrides: new PortalOverrides { RecommendedSignalExecutionEnabled = true },
            allowedSourceTypes: [SignalExecutionSourceType.Recommended]);

        await sut.RunOnceAsync(CancellationToken.None);

        queueService.Verify(s => s.EnqueueAsync(It.IsAny<TradeExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunOnceAsync_Skips_NonPending_And_Expired_Generated_And_Blocked_Signals()
    {
        var generatedHistory = new Mock<IGeneratedSignalHistoryService>();
        generatedHistory.Setup(s => s.GetHistoryAsync("ETHUSD", 100, 0, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GeneratedSignalHistoryPage
            {
                Signals =
                [
                    new GeneratedSignalWithOutcome
                    {
                        Signal = CreateGeneratedSignal() with { ExpiryTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-1) },
                        Outcome = CreateOutcome()
                    },
                    new GeneratedSignalWithOutcome
                    {
                        Signal = CreateGeneratedSignal(),
                        Outcome = CreateOutcome(OutcomeLabel.WIN)
                    }
                ],
                Stats = new PerformanceStats(),
                Total = 2,
                Page = 1,
                PageSize = 100
            });

        var blockedHistory = new Mock<IBlockedSignalHistoryService>();
        blockedHistory.Setup(s => s.GetHistoryAsync("ETHUSD", 100, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BlockedSignalHistoryPage
            {
                Signals =
                [
                    new BlockedSignalWithOutcome
                    {
                        Signal = CreateBlockedSignal() with { ExpiryTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-1) },
                        Outcome = CreateOutcome()
                    },
                    new BlockedSignalWithOutcome
                    {
                        Signal = CreateBlockedSignal(),
                        Outcome = CreateOutcome(OutcomeLabel.LOSS)
                    }
                ],
                Stats = new PerformanceStats(),
                Total = 2,
                Page = 1,
                PageSize = 100
            });

        var queueService = new Mock<ITradeExecutionQueueService>();
        var sut = CreateSut(
            generatedHistory: generatedHistory,
            blockedHistory: blockedHistory,
            queueService: queueService,
            allowedSourceTypes:
            [
                SignalExecutionSourceType.Generated,
                SignalExecutionSourceType.Blocked
            ]);

        await sut.RunOnceAsync(CancellationToken.None);

        queueService.Verify(s => s.EnqueueAsync(It.IsAny<TradeExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static TradeAutoExecutionService CreateSut(
        Mock<ISignalRepository>? signalRepo = null,
        Mock<IGeneratedSignalHistoryService>? generatedHistory = null,
        Mock<IBlockedSignalHistoryService>? blockedHistory = null,
        Mock<ITradeExecutionQueueService>? queueService = null,
        PortalOverrides? portalOverrides = null,
        IEnumerable<SignalExecutionSourceType>? allowedSourceTypes = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CapitalApi:Symbol"] = "ETHUSD"
            })
            .Build();

        var mapper = new ExecutionCandidateMapper();

        var policy = new Mock<ITradeExecutionPolicy>();
        policy.Setup(p => p.GetSettings()).Returns(new TradeExecutionPolicySettings
        {
            Enabled = true,
            AutoExecuteEnabled = true,
            AllowedSourceTypes = (allowedSourceTypes ?? [SignalExecutionSourceType.Recommended]).ToHashSet()
        });

        var executedRepo = new Mock<IExecutedTradeRepository>();
        executedRepo.Setup(r => r.GetBySourceSignalAsync(It.IsAny<Guid>(), It.IsAny<SignalExecutionSourceType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExecutedTrade?)null);

        var overridesRepo = new Mock<IPortalOverridesRepository>();
        overridesRepo.Setup(r => r.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(portalOverrides);

        return new TradeAutoExecutionService(
            config,
            signalRepo?.Object ?? Mock.Of<ISignalRepository>(),
            blockedHistory?.Object ?? Mock.Of<IBlockedSignalHistoryService>(),
            generatedHistory?.Object ?? Mock.Of<IGeneratedSignalHistoryService>(),
            mapper,
            policy.Object,
            queueService?.Object ?? Mock.Of<ITradeExecutionQueueService>(),
            executedRepo.Object,
            overridesRepo.Object,
            NullLogger<TradeAutoExecutionService>.Instance);
    }

    private static SignalRecommendation CreateRecommendedSignal() => new()
    {
        SignalId = Guid.NewGuid(),
        EvaluationId = Guid.NewGuid(),
        Symbol = "ETHUSD",
        Timeframe = "5m",
        SignalTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
        Direction = SignalDirection.BUY,
        EntryPrice = 2400m,
        TpPrice = 2410m,
        SlPrice = 2393m,
        RiskPercent = 0.5m,
        RiskUsd = 10m,
        ConfidenceScore = 80,
        Regime = Regime.BULLISH,
        StrategyVersion = "v3.1",
        Reasons = ["recommended"],
        Status = SignalStatus.OPEN
    };

    private static GeneratedSignalRecommendation CreateGeneratedSignal() => new()
    {
        SignalId = Guid.NewGuid(),
        EvaluationId = Guid.NewGuid(),
        Symbol = "ETHUSD",
        Timeframe = "15m",
        SignalTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-3),
        DecisionTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-3),
        BarTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-15),
        Direction = SignalDirection.SELL,
        LifecycleState = SignalLifecycleState.CANDIDATE_CREATED,
        Origin = DecisionOrigin.CLOSED_BAR,
        SourceMode = SourceMode.LIVE,
        Regime = Regime.BEARISH,
        StrategyVersion = "v3.1",
        Reasons = ["generated"],
        EntryPrice = 2405m,
        TpPrice = 2394m,
        SlPrice = 2411m,
        RiskPercent = 0.5m,
        RiskUsd = 10m,
        ConfidenceScore = 76,
        ExpiryBars = 20,
        ExpiryTimeUtc = DateTimeOffset.UtcNow.AddHours(1)
    };

    private static BlockedSignalRecommendation CreateBlockedSignal() => new()
    {
        SignalId = Guid.NewGuid(),
        EvaluationId = Guid.NewGuid(),
        Symbol = "ETHUSD",
        Timeframe = "30m",
        SignalTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-4),
        DecisionTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-4),
        BarTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-30),
        Direction = SignalDirection.BUY,
        LifecycleState = SignalLifecycleState.SESSION_BLOCKED,
        BlockReason = "MaxOpenPositions",
        Origin = DecisionOrigin.CLOSED_BAR,
        SourceMode = SourceMode.LIVE,
        Regime = Regime.BULLISH,
        StrategyVersion = "v3.1",
        Reasons = ["blocked"],
        EntryPrice = 2402m,
        TpPrice = 2414m,
        SlPrice = 2395m,
        RiskPercent = 0.5m,
        RiskUsd = 10m,
        ConfidenceScore = 74,
        ExpiryBars = 12,
        ExpiryTimeUtc = DateTimeOffset.UtcNow.AddHours(2)
    };

    private static SignalOutcome CreateOutcome(OutcomeLabel label = OutcomeLabel.PENDING) => new()
    {
        SignalId = Guid.NewGuid(),
        OutcomeLabel = label,
        BarsObserved = label == OutcomeLabel.PENDING ? 0 : 5,
        TpHit = label == OutcomeLabel.WIN,
        SlHit = label == OutcomeLabel.LOSS,
        PnlR = label switch
        {
            OutcomeLabel.WIN => 1.5m,
            OutcomeLabel.LOSS => -1.0m,
            _ => 0m
        },
        ClosedAtUtc = label == OutcomeLabel.PENDING ? null : DateTimeOffset.UtcNow
    };
}
