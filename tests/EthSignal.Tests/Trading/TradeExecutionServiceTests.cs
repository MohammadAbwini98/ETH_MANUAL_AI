using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using EthSignal.Infrastructure.Trading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EthSignal.Tests.Trading;

public sealed class TradeExecutionServiceTests
{
    [Fact]
    public async Task ExecuteAsync_WhenPolicyThrows_ReturnsFailedResultInsteadOfThrowing()
    {
        var candidate = new TradeExecutionCandidate
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
            ConfidenceScore = 82,
            Regime = Regime.BULLISH,
            Reasons = ["test"]
        };

        var policy = new Mock<ITradeExecutionPolicy>();
        policy.Setup(p => p.EvaluateAsync(It.IsAny<TradeExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Authentication failed (429): {\"errorCode\":\"error.too-many.requests\"}"));

        var capitalClient = new Mock<ICapitalTradingClient>();
        var repository = new Mock<IExecutedTradeRepository>();
        repository.Setup(r => r.InsertExecutedTradeAsync(It.IsAny<ExecutedTrade>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(99);
        repository.Setup(r => r.InsertExecutionAttemptAsync(It.IsAny<long?>(), It.IsAny<Guid>(), It.IsAny<SignalExecutionSourceType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository.Setup(r => r.InsertExecutionEventAsync(It.IsAny<long?>(), It.IsAny<Guid>(), It.IsAny<SignalExecutionSourceType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var runtimeState = new TradeExecutionRuntimeState();
        var sut = new TradeExecutionService(policy.Object, capitalClient.Object, repository.Object, runtimeState, NullLogger<TradeExecutionService>.Instance);

        var result = await sut.ExecuteAsync(new TradeExecutionRequest
        {
            Candidate = candidate,
            RequestedBy = "test"
        }, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Status.Should().Be(ExecutedTradeStatus.Failed);
        result.FailureReason.Should().Be("PolicyEvaluationFailed");
        result.ErrorDetails.Should().Contain("429");
        repository.Verify(r => r.InsertExecutedTradeAsync(It.Is<ExecutedTrade>(t => t.Status == ExecutedTradeStatus.Failed && t.FailureReason == "PolicyEvaluationFailed"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_PersistsDemoAiTradeMetadata()
    {
        var candidate = CreateCandidate(SignalExecutionSourceType.Recommended);

        var policy = new Mock<ITradeExecutionPolicy>();
        policy.Setup(p => p.EvaluateAsync(It.IsAny<TradeExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAllowedDecision(candidate));

        var capitalClient = new Mock<ICapitalTradingClient>();
        capitalClient.Setup(c => c.PlacePositionAsync(It.IsAny<CapitalPlacePositionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CapitalOpenPositionResult { DealReference = "deal-ref", Note = "submitted" });
        capitalClient.Setup(c => c.ConfirmDealAsync("deal-ref", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CapitalDealConfirmation
            {
                DealReference = "deal-ref",
                DealId = "deal-id",
                Accepted = true,
                DealStatus = "ACCEPTED",
                Status = "OPEN",
                Level = 2501m,
                Size = 0.1m
            });

        ExecutedTrade? insertedTrade = null;
        var repository = new Mock<IExecutedTradeRepository>();
        repository.Setup(r => r.InsertExecutedTradeAsync(It.IsAny<ExecutedTrade>(), It.IsAny<CancellationToken>()))
            .Callback((ExecutedTrade trade, CancellationToken _) => insertedTrade = trade)
            .ReturnsAsync(42);
        repository.Setup(r => r.UpdateExecutedTradeAsync(It.IsAny<ExecutedTrade>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository.Setup(r => r.InsertExecutionAttemptAsync(It.IsAny<long?>(), It.IsAny<Guid>(), It.IsAny<SignalExecutionSourceType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository.Setup(r => r.InsertExecutionEventAsync(It.IsAny<long?>(), It.IsAny<Guid>(), It.IsAny<SignalExecutionSourceType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var runtimeState = new TradeExecutionRuntimeState();
        var sut = new TradeExecutionService(policy.Object, capitalClient.Object, repository.Object, runtimeState, NullLogger<TradeExecutionService>.Instance);

        var result = await sut.ExecuteAsync(new TradeExecutionRequest
        {
            Candidate = candidate,
            RequestedBy = "test"
        }, CancellationToken.None);

        result.Success.Should().BeTrue();
        insertedTrade.Should().NotBeNull();
        insertedTrade!.AccountId.Should().Be("demo-1");
        insertedTrade.AccountName.Should().Be("DEMOAI");
        insertedTrade.IsDemo.Should().BeTrue();
        runtimeState.LatestExecutionAccountName.Should().Be("DEMOAI");
    }

    [Theory]
    [InlineData(SignalExecutionSourceType.Recommended)]
    [InlineData(SignalExecutionSourceType.Generated)]
    [InlineData(SignalExecutionSourceType.Blocked)]
    public async Task ExecuteAsync_Preserves_SourceType_For_All_Executable_SignalTypes(SignalExecutionSourceType sourceType)
    {
        var candidate = CreateCandidate(sourceType);
        var policy = new Mock<ITradeExecutionPolicy>();
        policy.Setup(p => p.EvaluateAsync(It.IsAny<TradeExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAllowedDecision(candidate));

        var capitalClient = new Mock<ICapitalTradingClient>();
        capitalClient.Setup(c => c.PlacePositionAsync(It.IsAny<CapitalPlacePositionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CapitalOpenPositionResult { DealReference = "deal-ref", Note = "submitted" });
        capitalClient.Setup(c => c.ConfirmDealAsync("deal-ref", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CapitalDealConfirmation
            {
                DealReference = "deal-ref",
                DealId = "deal-id",
                Accepted = true,
                DealStatus = "ACCEPTED",
                Status = "OPEN",
                Level = 2501m,
                Size = 0.1m
            });

        ExecutedTrade? insertedTrade = null;
        var repository = CreateRepository();
        repository.Setup(r => r.InsertExecutedTradeAsync(It.IsAny<ExecutedTrade>(), It.IsAny<CancellationToken>()))
            .Callback((ExecutedTrade trade, CancellationToken _) => insertedTrade = trade)
            .ReturnsAsync(42);

        var sut = new TradeExecutionService(
            policy.Object,
            capitalClient.Object,
            repository.Object,
            new TradeExecutionRuntimeState(),
            NullLogger<TradeExecutionService>.Instance);

        var result = await sut.ExecuteAsync(new TradeExecutionRequest
        {
            Candidate = candidate,
            RequestedBy = "test"
        }, CancellationToken.None);

        result.Success.Should().BeTrue();
        insertedTrade.Should().NotBeNull();
        insertedTrade!.SourceType.Should().Be(sourceType);
    }

    [Fact]
    public async Task ExecuteAsync_WhenInitialConfirmationIsStillPending_KeepsTradeSubmittedForReconciliation()
    {
        var candidate = CreateCandidate(SignalExecutionSourceType.Generated);
        var policy = new Mock<ITradeExecutionPolicy>();
        policy.Setup(p => p.EvaluateAsync(It.IsAny<TradeExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAllowedDecision(candidate));

        var capitalClient = new Mock<ICapitalTradingClient>();
        capitalClient.Setup(c => c.PlacePositionAsync(It.IsAny<CapitalPlacePositionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CapitalOpenPositionResult { DealReference = "deal-ref", Note = "submitted" });
        capitalClient.Setup(c => c.ConfirmDealAsync("deal-ref", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CapitalDealConfirmation
            {
                DealReference = "deal-ref",
                Accepted = true,
                DealStatus = "ACCEPTED",
                Status = "PENDING"
            });

        ExecutedTrade? updatedTrade = null;
        var repository = CreateRepository();
        repository.Setup(r => r.UpdateExecutedTradeAsync(It.IsAny<ExecutedTrade>(), It.IsAny<CancellationToken>()))
            .Callback((ExecutedTrade trade, CancellationToken _) => updatedTrade = trade)
            .Returns(Task.CompletedTask);

        var sut = new TradeExecutionService(
            policy.Object,
            capitalClient.Object,
            repository.Object,
            new TradeExecutionRuntimeState(),
            NullLogger<TradeExecutionService>.Instance);

        var result = await sut.ExecuteAsync(new TradeExecutionRequest
        {
            Candidate = candidate,
            RequestedBy = "test"
        }, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Status.Should().Be(ExecutedTradeStatus.Submitted);
        updatedTrade.Should().NotBeNull();
        updatedTrade!.Status.Should().Be(ExecutedTradeStatus.Submitted);
        updatedTrade.SourceType.Should().Be(SignalExecutionSourceType.Generated);
    }

    private static Mock<IExecutedTradeRepository> CreateRepository()
    {
        var repository = new Mock<IExecutedTradeRepository>();
        repository.Setup(r => r.InsertExecutedTradeAsync(It.IsAny<ExecutedTrade>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(42);
        repository.Setup(r => r.UpdateExecutedTradeAsync(It.IsAny<ExecutedTrade>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository.Setup(r => r.InsertExecutionAttemptAsync(It.IsAny<long?>(), It.IsAny<Guid>(), It.IsAny<SignalExecutionSourceType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository.Setup(r => r.InsertExecutionEventAsync(It.IsAny<long?>(), It.IsAny<Guid>(), It.IsAny<SignalExecutionSourceType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return repository;
    }

    private static TradeExecutionCandidate CreateCandidate(SignalExecutionSourceType sourceType) => new()
    {
        SignalId = Guid.NewGuid(),
        EvaluationId = Guid.NewGuid(),
        SourceType = sourceType,
        Symbol = "ETHUSD",
        Timeframe = "5m",
        SignalTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
        Direction = SignalDirection.BUY,
        RecommendedEntryPrice = 2500m,
        TpPrice = 2510m,
        SlPrice = 2490m,
        RiskPercent = 0.5m,
        RiskUsd = 10m,
        ConfidenceScore = 82,
        Regime = Regime.BULLISH,
        Reasons = ["test"]
    };

    private static TradeExecutionPolicyDecision CreateAllowedDecision(TradeExecutionCandidate candidate) => new()
    {
        Allowed = true,
        Message = "ok",
        Plan = new TradeExecutionPlan
        {
            Candidate = candidate,
            Epic = "ETHUSD",
            InstrumentName = "Ethereum/USD",
            RequestedSize = 0.1m,
            FinalSize = 0.1m,
            RequestedEntryPrice = 2500m,
            MarketEntryPrice = 2501m,
            ProfitLevel = 2510m,
            StopLevel = 2490m,
            Currency = "USD",
            ValidationNote = "ok",
            AccountSnapshot = new AccountSnapshot
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
            }
        }
    };
}
