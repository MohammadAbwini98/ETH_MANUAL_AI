using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using EthSignal.Infrastructure.Trading;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;

namespace EthSignal.Tests.Trading;

public sealed class TradeExecutionPolicyTests
{
    [Fact]
    public async Task EvaluateAsync_WhenActiveAccountIsLive_RejectsExecution()
    {
        var sut = CreateSut(
            snapshot: new AccountSnapshot
            {
                AccountId = "live-1",
                AccountName = "DEMOAI",
                Currency = "USD",
                Balance = 10000m,
                Equity = 10000m,
                Available = 9000m,
                Margin = 0m,
                Funds = 10000m,
                OpenPositions = 0,
                IsDemo = false,
                HedgingMode = false,
                CapturedAtUtc = DateTimeOffset.UtcNow
            });

        var result = await sut.EvaluateAsync(new TradeExecutionRequest
        {
            Candidate = CreateCandidate(),
            RequestedBy = "test"
        }, CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.FailureReason.Should().Be("AccountNotDemo");
    }

    [Fact]
    public async Task EvaluateAsync_WhenActiveDemoAccountMatchesDemoAi_UsesResolvedDemoContext()
    {
        var tradeRepo = new Mock<IExecutedTradeRepository>();
        tradeRepo.Setup(r => r.GetBySourceSignalAsync(It.IsAny<Guid>(), It.IsAny<SignalExecutionSourceType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExecutedTrade?)null);
        tradeRepo.Setup(r => r.GetActiveExecutedTradeCountAsync(It.IsAny<ExecutedTradeQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var capitalClient = new Mock<ICapitalTradingClient>();
        capitalClient.SetupGet(c => c.IsDemoEnvironment).Returns(true);
        capitalClient.Setup(c => c.EnsureDemoReadyAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        capitalClient.Setup(c => c.GetMarketInfoAsync("ETHUSD", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CapitalMarketInfo
            {
                Epic = "ETHUSD",
                Symbol = "ETHUSD",
                InstrumentName = "Ethereum/USD",
                Currency = "USD",
                Tradeable = true,
                Bid = 2500m,
                Offer = 2501m,
                DecimalPlaces = 2,
                MinDealSize = 0.1m,
                MinSizeIncrement = 0.1m,
                MinStopOrProfitDistance = 1m,
                MinStopOrProfitDistanceUnit = "POINTS",
                MarginFactor = 50m,
                MarginFactorUnit = "PERCENTAGE"
            });

        var snapshotService = new Mock<IAccountSnapshotService>();
        snapshotService.Setup(s => s.GetLatestAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountSnapshot
            {
                AccountId = "demo-1",
                AccountName = "DEMOAI",
                Currency = "USD",
                Balance = 10000m,
                Equity = 10000m,
                Available = 9000m,
                Margin = 0m,
                Funds = 10000m,
                OpenPositions = 0,
                IsDemo = true,
                HedgingMode = false,
                CapturedAtUtc = DateTimeOffset.UtcNow
            });

        var config = BuildConfig();
        var sut = new TradeExecutionPolicy(config, capitalClient.Object, tradeRepo.Object, snapshotService.Object);

        var result = await sut.EvaluateAsync(new TradeExecutionRequest
        {
            Candidate = CreateCandidate(),
            RequestedBy = "test"
        }, CancellationToken.None);

        result.Allowed.Should().BeTrue();
        result.Plan.Should().NotBeNull();
        result.Plan!.AccountSnapshot.AccountName.Should().Be("DEMOAI");
        result.Plan.AccountSnapshot.IsDemo.Should().BeTrue();
        tradeRepo.Verify(r => r.GetActiveExecutedTradeCountAsync(
            It.Is<ExecutedTradeQuery>(q => q.AccountId == "demo-1" && q.AccountName == "DEMOAI" && q.IsDemo == true),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EvaluateAsync_BuyTrade_Normalizes_Stop_To_BrokerBidSideBound()
    {
        var capitalClient = CreateCapitalClient(
            bid: 2500m,
            offer: 2501m,
            minDistance: 1m);
        var tradeRepo = CreateTradeRepo();
        var snapshotService = CreateSnapshotService();
        var sut = new TradeExecutionPolicy(BuildConfig(), capitalClient.Object, tradeRepo.Object, snapshotService.Object);

        var result = await sut.EvaluateAsync(new TradeExecutionRequest
        {
            Candidate = CreateCandidate() with
            {
                SourceType = SignalExecutionSourceType.Generated,
                TpPrice = 2502m,
                SlPrice = 2499.80m
            },
            RequestedBy = "test"
        }, CancellationToken.None);

        result.Allowed.Should().BeTrue();
        result.Plan.Should().NotBeNull();
        result.Plan!.StopLevel.Should().Be(2498.99m);
        result.Plan.ProfitLevel.Should().Be(2502.01m);
        result.Plan.ValidationNote.Should().Contain("broker max BUY stop");
    }

    [Fact]
    public async Task EvaluateAsync_SellTrade_Normalizes_Stop_To_BrokerOfferSideBound()
    {
        var capitalClient = CreateCapitalClient(
            bid: 2500m,
            offer: 2501m,
            minDistance: 1m);
        var tradeRepo = CreateTradeRepo();
        var snapshotService = CreateSnapshotService();
        var sut = new TradeExecutionPolicy(BuildConfig(), capitalClient.Object, tradeRepo.Object, snapshotService.Object);

        var result = await sut.EvaluateAsync(new TradeExecutionRequest
        {
            Candidate = CreateCandidate() with
            {
                SourceType = SignalExecutionSourceType.Generated,
                Direction = SignalDirection.SELL,
                RecommendedEntryPrice = 2500m,
                TpPrice = 2499m,
                SlPrice = 2501.20m
            },
            RequestedBy = "test"
        }, CancellationToken.None);

        result.Allowed.Should().BeTrue();
        result.Plan.Should().NotBeNull();
        result.Plan!.StopLevel.Should().Be(2502.01m);
        result.Plan.ProfitLevel.Should().Be(2498.99m);
        result.Plan.ValidationNote.Should().Contain("broker min SELL stop");
    }

    [Fact]
    public async Task EvaluateAsync_WithoutRequestedSize_UsesConfiguredDefaultTradeSize()
    {
        var capitalClient = CreateCapitalClient(
            bid: 2500m,
            offer: 2501m,
            minDistance: 1m,
            minDealSize: 0.01m,
            minSizeIncrement: 0.01m);
        var tradeRepo = CreateTradeRepo();
        var snapshotService = CreateSnapshotService();
        var sut = new TradeExecutionPolicy(BuildConfig(), capitalClient.Object, tradeRepo.Object, snapshotService.Object);

        var result = await sut.EvaluateAsync(new TradeExecutionRequest
        {
            Candidate = CreateCandidate() with
            {
                SourceType = SignalExecutionSourceType.Generated
            },
            RequestedBy = "test"
        }, CancellationToken.None);

        result.Allowed.Should().BeTrue();
        result.Plan.Should().NotBeNull();
        result.Plan!.RequestedSize.Should().Be(0.05m);
        result.Plan.FinalSize.Should().Be(0.05m);
    }

    [Fact]
    public async Task EvaluateAsync_WhenAbsoluteEntryDriftExceedsUsdMargin_RejectsExecution()
    {
        var capitalClient = CreateCapitalClient(
            bid: 2502m,
            offer: 2503m,
            minDistance: 1m,
            minDealSize: 0.01m,
            minSizeIncrement: 0.01m);
        var tradeRepo = CreateTradeRepo();
        var snapshotService = CreateSnapshotService();
        var sut = new TradeExecutionPolicy(BuildConfig(), capitalClient.Object, tradeRepo.Object, snapshotService.Object);

        var result = await sut.EvaluateAsync(new TradeExecutionRequest
        {
            Candidate = CreateCandidate() with
            {
                SourceType = SignalExecutionSourceType.Generated,
                RecommendedEntryPrice = 2500m
            },
            RequestedBy = "test"
        }, CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.FailureReason.Should().Be("EntryDriftExceeded");
        result.Message.Should().Contain("+/-1.00 USD");
    }

    [Fact]
    public async Task EvaluateAsync_WhenAbsoluteEntryDriftIsWithinUsdMargin_AllowsExecution()
    {
        var capitalClient = CreateCapitalClient(
            bid: 2499.6m,
            offer: 2500.8m,
            minDistance: 1m,
            minDealSize: 0.01m,
            minSizeIncrement: 0.01m);
        var tradeRepo = CreateTradeRepo();
        var snapshotService = CreateSnapshotService();
        var sut = new TradeExecutionPolicy(BuildConfig(), capitalClient.Object, tradeRepo.Object, snapshotService.Object);

        var result = await sut.EvaluateAsync(new TradeExecutionRequest
        {
            Candidate = CreateCandidate() with
            {
                SourceType = SignalExecutionSourceType.Blocked,
                RecommendedEntryPrice = 2500m
            },
            RequestedBy = "test"
        }, CancellationToken.None);

        result.Allowed.Should().BeTrue();
        result.Plan.Should().NotBeNull();
        result.Plan!.ValidationNote.Should().Contain("+/-1.00 USD");
        result.Plan.MarketEntryPrice.Should().Be(2500.8m);
    }

    [Theory]
    [InlineData(SignalExecutionSourceType.Recommended)]
    [InlineData(SignalExecutionSourceType.Generated)]
    [InlineData(SignalExecutionSourceType.Blocked)]
    public async Task EvaluateAsync_WhenTimeframeIs1m_RejectsExecutionForAllSourceTypes(SignalExecutionSourceType sourceType)
    {
        var capitalClient = CreateCapitalClient();
        var tradeRepo = CreateTradeRepo();
        var snapshotService = CreateSnapshotService();
        var sut = new TradeExecutionPolicy(BuildConfig(), capitalClient.Object, tradeRepo.Object, snapshotService.Object);

        var result = await sut.EvaluateAsync(new TradeExecutionRequest
        {
            Candidate = CreateCandidate() with
            {
                SourceType = sourceType,
                Timeframe = "1m"
            },
            RequestedBy = "test"
        }, CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.FailureReason.Should().Be("TimeframeNotAllowed");
        result.Message.Should().Contain("excluded from broker execution");
    }

    private static TradeExecutionPolicy CreateSut(AccountSnapshot snapshot)
    {
        var capitalClient = CreateCapitalClient();
        var tradeRepo = CreateTradeRepo();
        var snapshotService = new Mock<IAccountSnapshotService>();
        snapshotService.Setup(s => s.GetLatestAsync(It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);

        return new TradeExecutionPolicy(BuildConfig(), capitalClient.Object, tradeRepo.Object, snapshotService.Object);
    }

    private static IConfiguration BuildConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CapitalTrading:Enabled"] = "true",
            ["CapitalTrading:AutoExecuteEnabled"] = "true",
            ["CapitalTrading:DemoOnly"] = "true",
            ["CapitalTrading:PreferredDemoAccountName"] = "DEMOAI",
            ["CapitalTrading:AllowedSourceTypes"] = "Recommended,Generated,Blocked",
            ["CapitalTrading:EntryPriceMarginUsd"] = "1.0",
            ["CapitalTrading:DefaultTradeSize"] = "0.05",
            ["CapitalTrading:InstrumentEpicMap:ETHUSD"] = "ETHUSD",
            ["CapitalApi:Epic"] = "ETHUSD"
        })
        .Build();

    private static TradeExecutionCandidate CreateCandidate() => new()
    {
        SignalId = Guid.NewGuid(),
        EvaluationId = Guid.NewGuid(),
        SourceType = SignalExecutionSourceType.Recommended,
        Symbol = "ETHUSD",
        Timeframe = "5m",
        SignalTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
        Direction = SignalDirection.BUY,
        RecommendedEntryPrice = 2500m,
        TpPrice = 2510m,
        SlPrice = 2490m,
        RiskPercent = 0.5m,
        RiskUsd = 10m,
        ConfidenceScore = 80,
        Regime = Regime.BULLISH,
        Reasons = ["test"]
    };

    private static Mock<ICapitalTradingClient> CreateCapitalClient(
        decimal bid = 2500m,
        decimal offer = 2501m,
        decimal minDistance = 1m,
        decimal minDealSize = 0.1m,
        decimal minSizeIncrement = 0.1m)
    {
        var capitalClient = new Mock<ICapitalTradingClient>();
        capitalClient.SetupGet(c => c.IsDemoEnvironment).Returns(true);
        capitalClient.Setup(c => c.EnsureDemoReadyAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        capitalClient.Setup(c => c.GetMarketInfoAsync("ETHUSD", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CapitalMarketInfo
            {
                Epic = "ETHUSD",
                Symbol = "ETHUSD",
                InstrumentName = "Ethereum/USD",
                Currency = "USD",
                Tradeable = true,
                Bid = bid,
                Offer = offer,
                DecimalPlaces = 2,
                MinDealSize = minDealSize,
                MinSizeIncrement = minSizeIncrement,
                MinStopOrProfitDistance = minDistance,
                MinStopOrProfitDistanceUnit = "POINTS",
                MarginFactor = 50m,
                MarginFactorUnit = "PERCENTAGE"
            });
        return capitalClient;
    }

    private static Mock<IExecutedTradeRepository> CreateTradeRepo()
    {
        var tradeRepo = new Mock<IExecutedTradeRepository>();
        tradeRepo.Setup(r => r.GetBySourceSignalAsync(It.IsAny<Guid>(), It.IsAny<SignalExecutionSourceType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExecutedTrade?)null);
        tradeRepo.Setup(r => r.GetActiveExecutedTradeCountAsync(It.IsAny<ExecutedTradeQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        return tradeRepo;
    }

    private static Mock<IAccountSnapshotService> CreateSnapshotService()
    {
        var snapshotService = new Mock<IAccountSnapshotService>();
        snapshotService.Setup(s => s.GetLatestAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountSnapshot
            {
                AccountId = "demo-1",
                AccountName = "DEMOAI",
                Currency = "USD",
                Balance = 10000m,
                Equity = 10000m,
                Available = 9000m,
                Margin = 0m,
                Funds = 10000m,
                OpenPositions = 0,
                IsDemo = true,
                HedgingMode = false,
                CapturedAtUtc = DateTimeOffset.UtcNow
            });
        return snapshotService;
    }
}
