using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using EthSignal.Infrastructure.Trading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EthSignal.Tests.Trading;

public sealed class TradeLifecycleReconciliationServiceTests
{
    [Fact]
    public async Task RunOnceAsync_WhenSubmittedTradeAppearsInOpenPositions_TransitionsToOpen()
    {
        var trade = CreateTrade(ExecutedTradeStatus.Submitted, SignalExecutionSourceType.Generated) with
        {
            DealReference = "deal-ref",
            DealId = null
        };
        var repository = CreateRepository([trade]);

        ExecutedTrade? updatedTrade = null;
        repository.Setup(r => r.UpdateExecutedTradeAsync(It.IsAny<ExecutedTrade>(), It.IsAny<CancellationToken>()))
            .Callback((ExecutedTrade value, CancellationToken _) => updatedTrade = value)
            .Returns(Task.CompletedTask);

        var capitalClient = new Mock<ICapitalTradingClient>();
        capitalClient.Setup(c => c.GetOpenPositionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new CapitalPositionSnapshot
                {
                    DealId = "deal-id",
                    DealReference = "deal-ref",
                    Epic = "ETHUSD",
                    Direction = SignalDirection.BUY,
                    Size = 0.2m,
                    Level = 2502m,
                    StopLevel = 2490m,
                    ProfitLevel = 2510m,
                    Currency = "USD"
                }
            ]);

        var sut = new TradeLifecycleReconciliationService(
            capitalClient.Object,
            repository.Object,
            NullLogger<TradeLifecycleReconciliationService>.Instance);

        await sut.RunOnceAsync(CancellationToken.None);

        updatedTrade.Should().NotBeNull();
        updatedTrade!.Status.Should().Be(ExecutedTradeStatus.Open);
        updatedTrade.DealId.Should().Be("deal-id");
        updatedTrade.SourceType.Should().Be(SignalExecutionSourceType.Generated);
    }

    [Fact]
    public async Task RunOnceAsync_WhenOpenTradeClosesViaTp_TransitionsToWin()
    {
        var trade = CreateTrade(ExecutedTradeStatus.Open, SignalExecutionSourceType.Recommended);
        var repository = CreateRepository([trade]);

        ExecutedTrade? updatedTrade = null;
        repository.Setup(r => r.UpdateExecutedTradeAsync(It.IsAny<ExecutedTrade>(), It.IsAny<CancellationToken>()))
            .Callback((ExecutedTrade value, CancellationToken _) => updatedTrade = value)
            .Returns(Task.CompletedTask);

        var capitalClient = new Mock<ICapitalTradingClient>();
        capitalClient.Setup(c => c.GetOpenPositionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<CapitalPositionSnapshot>());
        capitalClient.Setup(c => c.GetPositionAsync("deal-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync((CapitalPositionSnapshot?)null);
        capitalClient.Setup(c => c.GetActivityHistoryAsync(It.IsAny<CapitalActivityQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new CapitalActivityRecord
                {
                    DateUtc = DateTimeOffset.UtcNow,
                    DealId = "deal-id",
                    Epic = "ETHUSD",
                    Source = "TP",
                    Type = "POSITION",
                    Status = "EXECUTED",
                    Level = 2510m,
                    Size = 0.1m,
                    Currency = "USD"
                }
            ]);

        var sut = new TradeLifecycleReconciliationService(
            capitalClient.Object,
            repository.Object,
            NullLogger<TradeLifecycleReconciliationService>.Instance);

        await sut.RunOnceAsync(CancellationToken.None);

        updatedTrade.Should().NotBeNull();
        updatedTrade!.Status.Should().Be(ExecutedTradeStatus.Win);
        updatedTrade.CloseSource.Should().Be(TradeCloseSource.TakeProfit);
        updatedTrade.Pnl.Should().BePositive();
    }

    [Fact]
    public async Task RunOnceAsync_WhenOpenTradeClosesViaSl_TransitionsToLoss()
    {
        var trade = CreateTrade(ExecutedTradeStatus.Open, SignalExecutionSourceType.Blocked);
        var repository = CreateRepository([trade]);

        ExecutedTrade? updatedTrade = null;
        repository.Setup(r => r.UpdateExecutedTradeAsync(It.IsAny<ExecutedTrade>(), It.IsAny<CancellationToken>()))
            .Callback((ExecutedTrade value, CancellationToken _) => updatedTrade = value)
            .Returns(Task.CompletedTask);

        var capitalClient = new Mock<ICapitalTradingClient>();
        capitalClient.Setup(c => c.GetOpenPositionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<CapitalPositionSnapshot>());
        capitalClient.Setup(c => c.GetPositionAsync("deal-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync((CapitalPositionSnapshot?)null);
        capitalClient.Setup(c => c.GetActivityHistoryAsync(It.IsAny<CapitalActivityQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new CapitalActivityRecord
                {
                    DateUtc = DateTimeOffset.UtcNow,
                    DealId = "deal-id",
                    Epic = "ETHUSD",
                    Source = "SL",
                    Type = "POSITION",
                    Status = "EXECUTED",
                    Level = 2490m,
                    Size = 0.1m,
                    Currency = "USD"
                }
            ]);

        var sut = new TradeLifecycleReconciliationService(
            capitalClient.Object,
            repository.Object,
            NullLogger<TradeLifecycleReconciliationService>.Instance);

        await sut.RunOnceAsync(CancellationToken.None);

        updatedTrade.Should().NotBeNull();
        updatedTrade!.Status.Should().Be(ExecutedTradeStatus.Loss);
        updatedTrade.CloseSource.Should().Be(TradeCloseSource.StopLoss);
        updatedTrade.Pnl.Should().BeNegative();
        updatedTrade.SourceType.Should().Be(SignalExecutionSourceType.Blocked);
    }

    [Fact]
    public async Task RunOnceAsync_WhenThereAreNoActiveTrades_DoesNotPollBroker()
    {
        var repository = CreateRepository([]);
        var capitalClient = new Mock<ICapitalTradingClient>(MockBehavior.Strict);

        var sut = new TradeLifecycleReconciliationService(
            capitalClient.Object,
            repository.Object,
            NullLogger<TradeLifecycleReconciliationService>.Instance);

        await sut.RunOnceAsync(CancellationToken.None);

        capitalClient.Verify(c => c.GetOpenPositionsAsync(It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(r => r.UpdateExecutedTradeAsync(It.IsAny<ExecutedTrade>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunOnceAsync_WhenBrokerCloseIsUnconfirmed_DoesNotAssignTerminalStatus()
    {
        var trade = CreateTrade(ExecutedTradeStatus.Open, SignalExecutionSourceType.Generated);
        var repository = CreateRepository([trade]);

        var capitalClient = new Mock<ICapitalTradingClient>();
        capitalClient.Setup(c => c.GetOpenPositionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<CapitalPositionSnapshot>());
        capitalClient.Setup(c => c.GetPositionAsync("deal-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync((CapitalPositionSnapshot?)null);
        capitalClient.Setup(c => c.GetActivityHistoryAsync(It.IsAny<CapitalActivityQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<CapitalActivityRecord>());

        var sut = new TradeLifecycleReconciliationService(
            capitalClient.Object,
            repository.Object,
            NullLogger<TradeLifecycleReconciliationService>.Instance);

        await sut.RunOnceAsync(CancellationToken.None);

        repository.Verify(
            r => r.UpdateExecutedTradeAsync(
                It.Is<ExecutedTrade>(value =>
                    value.Status == ExecutedTradeStatus.Win
                    || value.Status == ExecutedTradeStatus.Loss
                    || value.Status == ExecutedTradeStatus.Closed),
                It.IsAny<CancellationToken>()),
            Times.Never);
        repository.Verify(
            r => r.InsertExecutionEventAsync(
                trade.ExecutedTradeId,
                trade.SignalId,
                trade.SourceType,
                "close_unconfirmed",
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunOnceAsync_WhenActivityLooksLikeOpenFillWithZeroLevel_DoesNotForceLoss()
    {
        var trade = CreateTrade(ExecutedTradeStatus.Open, SignalExecutionSourceType.Recommended) with
        {
            OpenedAtUtc = new DateTimeOffset(new DateTime(2026, 4, 20, 18, 10, 8, 636, DateTimeKind.Utc))
        };
        var repository = CreateRepository([trade]);

        var capitalClient = new Mock<ICapitalTradingClient>();
        capitalClient.Setup(c => c.GetOpenPositionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<CapitalPositionSnapshot>());
        capitalClient.Setup(c => c.GetPositionAsync("deal-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync((CapitalPositionSnapshot?)null);
        capitalClient.Setup(c => c.GetActivityHistoryAsync(It.IsAny<CapitalActivityQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new CapitalActivityRecord
                {
                    DateUtc = new DateTimeOffset(new DateTime(2026, 4, 20, 18, 10, 8, 565, DateTimeKind.Utc)),
                    DealId = "deal-id",
                    Epic = "ETHUSD",
                    Source = "USER",
                    Type = "POSITION",
                    Status = "EXECUTED",
                    Level = 0m,
                    Size = 0.05m,
                    Currency = "USD"
                }
            ]);

        var sut = new TradeLifecycleReconciliationService(
            capitalClient.Object,
            repository.Object,
            NullLogger<TradeLifecycleReconciliationService>.Instance);

        await sut.RunOnceAsync(CancellationToken.None);

        repository.Verify(
            r => r.UpdateExecutedTradeAsync(
                It.Is<ExecutedTrade>(value =>
                    value.Status == ExecutedTradeStatus.Win
                    || value.Status == ExecutedTradeStatus.Loss
                    || value.Status == ExecutedTradeStatus.Closed),
                It.IsAny<CancellationToken>()),
            Times.Never);
        repository.Verify(
            r => r.InsertExecutionEventAsync(
                trade.ExecutedTradeId,
                trade.SignalId,
                trade.SourceType,
                "close_unconfirmed",
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunOnceAsync_WhenManualBrokerCloseHasValidLevel_ComputesRealTerminalStatus()
    {
        var trade = CreateTrade(ExecutedTradeStatus.Open, SignalExecutionSourceType.Blocked) with
        {
            Direction = SignalDirection.SELL
        };
        var repository = CreateRepository([trade]);

        ExecutedTrade? updatedTrade = null;
        repository.Setup(r => r.UpdateExecutedTradeAsync(It.IsAny<ExecutedTrade>(), It.IsAny<CancellationToken>()))
            .Callback((ExecutedTrade value, CancellationToken _) => updatedTrade = value)
            .Returns(Task.CompletedTask);

        var capitalClient = new Mock<ICapitalTradingClient>();
        capitalClient.Setup(c => c.GetOpenPositionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<CapitalPositionSnapshot>());
        capitalClient.Setup(c => c.GetPositionAsync("deal-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync((CapitalPositionSnapshot?)null);
        capitalClient.Setup(c => c.GetActivityHistoryAsync(It.IsAny<CapitalActivityQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new CapitalActivityRecord
                {
                    DateUtc = DateTimeOffset.UtcNow,
                    DealId = "deal-id",
                    Epic = "ETHUSD",
                    Source = "USER",
                    Type = "POSITION",
                    Status = "EXECUTED",
                    Level = 2485m,
                    Size = 0.1m,
                    Currency = "USD"
                }
            ]);

        var sut = new TradeLifecycleReconciliationService(
            capitalClient.Object,
            repository.Object,
            NullLogger<TradeLifecycleReconciliationService>.Instance);

        await sut.RunOnceAsync(CancellationToken.None);

        updatedTrade.Should().NotBeNull();
        updatedTrade!.Status.Should().Be(ExecutedTradeStatus.Win);
        updatedTrade.CloseSource.Should().Be(TradeCloseSource.User);
        updatedTrade.Pnl.Should().BePositive();
    }

    private static Mock<IExecutedTradeRepository> CreateRepository(IReadOnlyList<ExecutedTrade> trades)
    {
        var repository = new Mock<IExecutedTradeRepository>();
        repository.Setup(r => r.GetTradesForLifecycleReconciliationAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(trades);
        repository.Setup(r => r.UpdateExecutedTradeAsync(It.IsAny<ExecutedTrade>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository.Setup(r => r.InsertExecutionEventAsync(It.IsAny<long?>(), It.IsAny<Guid>(), It.IsAny<SignalExecutionSourceType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return repository;
    }

    private static ExecutedTrade CreateTrade(ExecutedTradeStatus status, SignalExecutionSourceType sourceType) => new()
    {
        ExecutedTradeId = 7,
        SignalId = Guid.NewGuid(),
        EvaluationId = Guid.NewGuid(),
        SourceType = sourceType,
        Symbol = "ETHUSD",
        Instrument = "ETHUSD",
        Timeframe = "5m",
        Direction = SignalDirection.BUY,
        RecommendedEntryPrice = 2500m,
        ActualEntryPrice = 2500m,
        TpPrice = 2510m,
        SlPrice = 2490m,
        RequestedSize = 0.1m,
        ExecutedSize = 0.1m,
        DealReference = "deal-ref",
        DealId = "deal-id",
        Status = status,
        AccountId = "demo-1",
        AccountName = "DEMOAI",
        IsDemo = true,
        AccountCurrency = "USD",
        OpenedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
        CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-6),
        UpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
    };
}
