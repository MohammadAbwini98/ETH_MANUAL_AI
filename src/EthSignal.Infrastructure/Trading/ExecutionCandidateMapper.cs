using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Engine;

namespace EthSignal.Infrastructure.Trading;

/// <summary>
/// Implementation note:
/// 1. Rule-based directional intent is created in SignalEngine.EvaluateWithDecision(...).
/// 2. Entry/TP/SL are finalized later in LiveTickProcessor via RiskManager.EstimateLiveFillPrice(...)
///    plus ExitEngine.Compute(...), then persisted as SignalRecommendation rows.
/// 3. Recommended signals therefore use the persisted signal table as the execution source of truth.
/// 4. Generated and blocked histories already reconstruct recommendation-shaped records with
///    SignalId, Entry, TP, SL, timeframe, direction, and reasons, so we map those directly too.
/// 5. This keeps broker execution loosely coupled: execution consumes a normalized candidate contract
///    and never depends on SignalEngine internals or Capital.com DTOs.
/// </summary>
public sealed class ExecutionCandidateMapper : IExecutionCandidateMapper
{
    public TradeExecutionCandidate FromRecommended(SignalRecommendation signal) => new()
    {
        SignalId = signal.SignalId,
        EvaluationId = signal.EvaluationId,
        SourceType = SignalExecutionSourceType.Recommended,
        Symbol = signal.Symbol,
        Timeframe = signal.Timeframe,
        SignalTimeUtc = signal.SignalTimeUtc,
        Direction = signal.Direction,
        RecommendedEntryPrice = signal.EntryPrice,
        TpPrice = signal.TpPrice,
        SlPrice = signal.SlPrice,
        RiskPercent = signal.RiskPercent,
        RiskUsd = signal.RiskUsd,
        ConfidenceScore = signal.ConfidenceScore,
        Regime = signal.Regime,
        Reasons = signal.Reasons,
        StrategyVersion = signal.StrategyVersion,
        ExitModel = signal.ExitModel,
        ExitExplanation = signal.ExitExplanation
    };

    public TradeExecutionCandidate FromGenerated(GeneratedSignalRecommendation signal) => new()
    {
        SignalId = signal.SignalId,
        EvaluationId = signal.EvaluationId,
        SourceType = SignalExecutionSourceType.Generated,
        Symbol = signal.Symbol,
        Timeframe = signal.Timeframe,
        SignalTimeUtc = signal.SignalTimeUtc,
        Direction = signal.Direction,
        RecommendedEntryPrice = signal.EntryPrice,
        TpPrice = signal.TpPrice,
        SlPrice = signal.SlPrice,
        RiskPercent = signal.RiskPercent,
        RiskUsd = signal.RiskUsd,
        ConfidenceScore = signal.ConfidenceScore,
        Regime = signal.Regime,
        Reasons = signal.Reasons,
        StrategyVersion = signal.StrategyVersion,
        ExitModel = signal.ExitModel,
        ExitExplanation = signal.ExitExplanation
    };

    public TradeExecutionCandidate FromBlocked(BlockedSignalRecommendation signal) => new()
    {
        SignalId = signal.SignalId,
        EvaluationId = signal.EvaluationId,
        SourceType = SignalExecutionSourceType.Blocked,
        Symbol = signal.Symbol,
        Timeframe = signal.Timeframe,
        SignalTimeUtc = signal.SignalTimeUtc,
        Direction = signal.Direction,
        RecommendedEntryPrice = signal.EntryPrice,
        TpPrice = signal.TpPrice,
        SlPrice = signal.SlPrice,
        RiskPercent = signal.RiskPercent,
        RiskUsd = signal.RiskUsd,
        ConfidenceScore = signal.ConfidenceScore,
        Regime = signal.Regime,
        Reasons = signal.Reasons,
        StrategyVersion = signal.StrategyVersion,
        ExitModel = signal.ExitModel,
        ExitExplanation = signal.ExitExplanation
    };
}
