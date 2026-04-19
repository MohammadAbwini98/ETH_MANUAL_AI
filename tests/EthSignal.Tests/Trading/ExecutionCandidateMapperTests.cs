using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Engine;
using EthSignal.Infrastructure.Trading;
using FluentAssertions;

namespace EthSignal.Tests.Trading;

public sealed class ExecutionCandidateMapperTests
{
    private readonly ExecutionCandidateMapper _sut = new();

    [Fact]
    public void FromRecommended_Preserves_Source_And_Core_Fields()
    {
        var signal = new SignalRecommendation
        {
            SignalId = Guid.NewGuid(),
            EvaluationId = Guid.NewGuid(),
            Symbol = "ETHUSD",
            Timeframe = "5m",
            SignalTimeUtc = new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero),
            Direction = SignalDirection.BUY,
            EntryPrice = 2450m,
            TpPrice = 2462m,
            SlPrice = 2442m,
            RiskPercent = 0.5m,
            RiskUsd = 10m,
            ConfidenceScore = 82,
            Regime = Regime.BULLISH,
            StrategyVersion = "v3.1",
            Reasons = ["trend", "breakout"],
            Status = SignalStatus.OPEN,
            ExitModel = "structure",
            ExitExplanation = "trend continuation"
        };

        var candidate = _sut.FromRecommended(signal);

        candidate.SourceType.Should().Be(SignalExecutionSourceType.Recommended);
        candidate.SignalId.Should().Be(signal.SignalId);
        candidate.EvaluationId.Should().Be(signal.EvaluationId);
        candidate.Symbol.Should().Be("ETHUSD");
        candidate.Timeframe.Should().Be("5m");
        candidate.Direction.Should().Be(SignalDirection.BUY);
        candidate.RecommendedEntryPrice.Should().Be(2450m);
        candidate.TpPrice.Should().Be(2462m);
        candidate.SlPrice.Should().Be(2442m);
        candidate.ConfidenceScore.Should().Be(82);
        candidate.ExitModel.Should().Be("structure");
    }

    [Fact]
    public void FromGenerated_Preserves_Generated_Source()
    {
        var signal = new GeneratedSignalRecommendation
        {
            SignalId = Guid.NewGuid(),
            EvaluationId = Guid.NewGuid(),
            Symbol = "ETHUSD",
            Timeframe = "15m",
            SignalTimeUtc = new DateTimeOffset(2026, 4, 19, 12, 15, 0, TimeSpan.Zero),
            DecisionTimeUtc = new DateTimeOffset(2026, 4, 19, 12, 15, 5, TimeSpan.Zero),
            BarTimeUtc = new DateTimeOffset(2026, 4, 19, 12, 15, 0, TimeSpan.Zero),
            Direction = SignalDirection.SELL,
            LifecycleState = SignalLifecycleState.CANDIDATE_CREATED,
            Origin = DecisionOrigin.SCALP_1M,
            SourceMode = SourceMode.LIVE,
            Regime = Regime.BEARISH,
            StrategyVersion = "v3.1",
            Reasons = ["generated"],
            EntryPrice = 2448m,
            TpPrice = 2438m,
            SlPrice = 2455m,
            RiskPercent = 0.4m,
            RiskUsd = 8m,
            ConfidenceScore = 75,
            ExpiryBars = 12,
            ExpiryTimeUtc = new DateTimeOffset(2026, 4, 19, 15, 15, 0, TimeSpan.Zero),
            ExitModel = "adaptive"
        };

        var candidate = _sut.FromGenerated(signal);

        candidate.SourceType.Should().Be(SignalExecutionSourceType.Generated);
        candidate.Direction.Should().Be(SignalDirection.SELL);
        candidate.RecommendedEntryPrice.Should().Be(2448m);
        candidate.TpPrice.Should().Be(2438m);
        candidate.SlPrice.Should().Be(2455m);
        candidate.ExitModel.Should().Be("adaptive");
    }

    [Fact]
    public void FromBlocked_Preserves_Blocked_Source()
    {
        var signal = new BlockedSignalRecommendation
        {
            SignalId = Guid.NewGuid(),
            EvaluationId = Guid.NewGuid(),
            Symbol = "ETHUSD",
            Timeframe = "30m",
            SignalTimeUtc = new DateTimeOffset(2026, 4, 19, 13, 0, 0, TimeSpan.Zero),
            DecisionTimeUtc = new DateTimeOffset(2026, 4, 19, 13, 0, 4, TimeSpan.Zero),
            BarTimeUtc = new DateTimeOffset(2026, 4, 19, 13, 0, 0, TimeSpan.Zero),
            Direction = SignalDirection.BUY,
            LifecycleState = SignalLifecycleState.SESSION_BLOCKED,
            BlockReason = "MaxOpenPositions",
            Origin = DecisionOrigin.SCALP_1M,
            SourceMode = SourceMode.LIVE,
            Regime = Regime.BULLISH,
            StrategyVersion = "v3.1",
            Reasons = ["blocked"],
            EntryPrice = 2460m,
            TpPrice = 2474m,
            SlPrice = 2451m,
            RiskPercent = 0.3m,
            RiskUsd = 6m,
            ConfidenceScore = 71,
            ExpiryBars = 8,
            ExpiryTimeUtc = new DateTimeOffset(2026, 4, 19, 17, 0, 0, TimeSpan.Zero),
            ExitExplanation = "session gate"
        };

        var candidate = _sut.FromBlocked(signal);

        candidate.SourceType.Should().Be(SignalExecutionSourceType.Blocked);
        candidate.SignalId.Should().Be(signal.SignalId);
        candidate.Direction.Should().Be(SignalDirection.BUY);
        candidate.Reasons.Should().ContainSingle().Which.Should().Be("blocked");
        candidate.ExitExplanation.Should().Be("session gate");
    }
}
