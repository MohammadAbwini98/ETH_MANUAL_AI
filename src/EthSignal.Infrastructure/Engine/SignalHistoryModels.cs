using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Engine;

public enum SignalHistorySortColumn
{
    Time,
    Source,
    Timeframe,
    Direction,
    Entry,
    Tp,
    Sl,
    Score,
    Outcome,
    Pnl
}

public enum SignalHistorySortDirection
{
    Asc,
    Desc
}

public sealed record SignalHistoryQuery
{
    public required string Symbol { get; init; }
    public SignalExecutionSourceType? SourceType { get; init; }
    public string? Timeframe { get; init; }
    public SignalDirection? Direction { get; init; }
    public string? Outcome { get; init; }
    public DateOnly? DateFrom { get; init; }
    public DateOnly? DateTo { get; init; }
    public SignalHistorySortColumn SortBy { get; init; } = SignalHistorySortColumn.Time;
    public SignalHistorySortDirection SortDirection { get; init; } = SignalHistorySortDirection.Desc;
    public int Limit { get; init; } = 50;
    public int Offset { get; init; }
}

public sealed record SignalHistorySignal
{
    public required Guid SignalId { get; init; }
    public Guid? EvaluationId { get; init; }
    public required SignalExecutionSourceType SourceType { get; init; }
    public required string Source { get; init; }
    public required string Symbol { get; init; }
    public required string Timeframe { get; init; }
    public required DateTimeOffset SignalTimeUtc { get; init; }
    public DateTimeOffset? DecisionTimeUtc { get; init; }
    public DateTimeOffset? BarTimeUtc { get; init; }
    public required SignalDirection Direction { get; init; }
    public SignalStatus? Status { get; init; }
    public SignalLifecycleState? LifecycleState { get; init; }
    public required Regime Regime { get; init; }
    public string? StrategyVersion { get; init; }
    public required IReadOnlyList<string> Reasons { get; init; }
    public decimal EntryPrice { get; init; }
    public decimal TpPrice { get; init; }
    public decimal SlPrice { get; init; }
    public decimal RiskPercent { get; init; }
    public decimal RiskUsd { get; init; }
    public int ConfidenceScore { get; init; }
    public string? BlockReason { get; init; }
    public string? ExitModel { get; init; }
    public string? ExitExplanation { get; init; }
    public int? ExpiryBars { get; init; }
    public DateTimeOffset? ExpiryTimeUtc { get; init; }
}

public sealed record SignalHistoryEntry
{
    public required SignalHistorySignal Signal { get; init; }
    public SignalOutcome? Outcome { get; init; }
    public ExecutedTrade? Execution { get; init; }
}

public sealed record SignalHistoryPage
{
    public required IReadOnlyList<SignalHistoryEntry> Signals { get; init; }
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}

public interface ISignalHistoryService
{
    Task<SignalHistoryPage> GetHistoryAsync(SignalHistoryQuery query, CancellationToken ct = default);
    Task<TradeExecutionCandidate?> GetExecutionCandidateAsync(
        string symbol,
        SignalExecutionSourceType sourceType,
        Guid signalId,
        CancellationToken ct = default);
}
