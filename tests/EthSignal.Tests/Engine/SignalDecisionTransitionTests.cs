using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Engine;
using FluentAssertions;

namespace EthSignal.Tests.Engine;

public sealed class SignalDecisionTransitionTests
{
    [Fact]
    public void ToOperationalBlock_NormalizesDirectionalDecision_ToBlockedNoTrade()
    {
        var decision = new SignalDecision
        {
            Symbol = "ETHUSD",
            Timeframe = "5m",
            DecisionTimeUtc = new DateTimeOffset(2026, 4, 21, 10, 45, 0, TimeSpan.Zero),
            BarTimeUtc = new DateTimeOffset(2026, 4, 21, 10, 40, 0, TimeSpan.Zero),
            DecisionType = SignalDirection.BUY,
            OutcomeCategory = OutcomeCategory.SIGNAL_GENERATED,
            LifecycleState = SignalLifecycleState.CANDIDATE_CREATED,
            FinalBlockReason = null,
            UsedRegime = Regime.BULLISH,
            ReasonCodes = [RejectReasonCode.SCORE_BELOW_THRESHOLD],
            ReasonDetails = ["candidate created"],
            IndicatorSnapshot = new Dictionary<string, decimal>(),
            ConfidenceScore = 80,
            ParameterSetId = "v3.1",
            SourceMode = SourceMode.LIVE
        };

        var blocked = SignalDecisionTransition.ToOperationalBlock(
            decision,
            SignalLifecycleState.RISK_BLOCKED,
            "Exit engine rejected",
            [RejectReasonCode.RISK_MANAGER_BLOCKED],
            ["Exit engine blocked signal: Exit engine rejected"]);

        blocked.DecisionType.Should().Be(SignalDirection.NO_TRADE);
        blocked.OutcomeCategory.Should().Be(OutcomeCategory.OPERATIONAL_BLOCKED);
        blocked.LifecycleState.Should().Be(SignalLifecycleState.RISK_BLOCKED);
        blocked.FinalBlockReason.Should().Be("Exit engine rejected");
        blocked.CandidateDirection.Should().Be(SignalDirection.BUY);
        blocked.ReasonCodes.Should().Contain(RejectReasonCode.SCORE_BELOW_THRESHOLD);
        blocked.ReasonCodes.Should().Contain(RejectReasonCode.RISK_MANAGER_BLOCKED);
        blocked.ReasonDetails.Should().Contain("Exit engine blocked signal: Exit engine rejected");
    }

    [Fact]
    public void ToOperationalBlock_PreservesExistingCandidateDirection()
    {
        var decision = new SignalDecision
        {
            Symbol = "ETHUSD",
            Timeframe = "15m",
            DecisionTimeUtc = new DateTimeOffset(2026, 4, 21, 10, 45, 0, TimeSpan.Zero),
            BarTimeUtc = new DateTimeOffset(2026, 4, 21, 10, 30, 0, TimeSpan.Zero),
            DecisionType = SignalDirection.NO_TRADE,
            OutcomeCategory = OutcomeCategory.SIGNAL_GENERATED,
            LifecycleState = SignalLifecycleState.CANDIDATE_CREATED,
            FinalBlockReason = null,
            UsedRegime = Regime.BEARISH,
            ReasonCodes = [RejectReasonCode.CONFLICTING_SIGNALS],
            ReasonDetails = ["candidate blocked"],
            IndicatorSnapshot = new Dictionary<string, decimal>(),
            ConfidenceScore = 74,
            ParameterSetId = "v3.1",
            SourceMode = SourceMode.LIVE,
            CandidateDirection = SignalDirection.SELL
        };

        var blocked = SignalDecisionTransition.ToOperationalBlock(
            decision,
            SignalLifecycleState.SESSION_BLOCKED,
            "Session limit reached");

        blocked.CandidateDirection.Should().Be(SignalDirection.SELL);
        blocked.DecisionType.Should().Be(SignalDirection.NO_TRADE);
        blocked.OutcomeCategory.Should().Be(OutcomeCategory.OPERATIONAL_BLOCKED);
        blocked.ReasonDetails.Should().Contain("Operational block: Session limit reached");
    }
}
