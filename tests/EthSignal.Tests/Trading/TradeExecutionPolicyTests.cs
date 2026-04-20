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
        tradeRepo.Setup(r => r.GetOpenExecutedTradeCountAsync(It.IsAny<ExecutedTradeQuery>(), It.IsAny<CancellationToken>()))
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
        tradeRepo.Verify(r => r.GetOpenExecutedTradeCountAsync(
            It.Is<ExecutedTradeQuery>(q => q.AccountId == "demo-1" && q.AccountName == "DEMOAI" && q.IsDemo == true),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static TradeExecutionPolicy CreateSut(AccountSnapshot snapshot)
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

        var tradeRepo = new Mock<IExecutedTradeRepository>();
        tradeRepo.Setup(r => r.GetBySourceSignalAsync(It.IsAny<Guid>(), It.IsAny<SignalExecutionSourceType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExecutedTrade?)null);

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
            ["CapitalTrading:AllowedSourceTypes"] = "Recommended",
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
}