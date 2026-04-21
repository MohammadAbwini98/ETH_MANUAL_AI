using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Engine;

public static class SignalDecisionTransition
{
    public static SignalDecision ToOperationalBlock(
        SignalDecision decision,
        SignalLifecycleState lifecycleState,
        string? finalBlockReason,
        IReadOnlyList<RejectReasonCode>? additionalReasonCodes = null,
        IReadOnlyList<string>? additionalReasonDetails = null)
    {
        var candidateDirection = decision.CandidateDirection
            ?? (decision.DecisionType is SignalDirection.BUY or SignalDirection.SELL
                ? decision.DecisionType
                : null);

        var mergedReasonCodes = decision.ReasonCodes
            .Concat(additionalReasonCodes ?? Array.Empty<RejectReasonCode>())
            .Distinct()
            .ToList();

        var defaultDetail = string.IsNullOrWhiteSpace(finalBlockReason)
            ? Array.Empty<string>()
            : [$"Operational block: {finalBlockReason}"];

        var mergedReasonDetails = decision.ReasonDetails
            .Concat(additionalReasonDetails ?? defaultDetail)
            .Where(detail => !string.IsNullOrWhiteSpace(detail))
            .Distinct()
            .ToList();

        return decision with
        {
            DecisionType = SignalDirection.NO_TRADE,
            OutcomeCategory = OutcomeCategory.OPERATIONAL_BLOCKED,
            LifecycleState = lifecycleState,
            FinalBlockReason = finalBlockReason,
            CandidateDirection = candidateDirection,
            ReasonCodes = mergedReasonCodes,
            ReasonDetails = mergedReasonDetails
        };
    }
}
