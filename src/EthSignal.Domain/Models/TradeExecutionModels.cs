namespace EthSignal.Domain.Models;

public enum SignalExecutionSourceType
{
    Recommended,
    Generated,
    Blocked
}

public enum ExecutedTradeStatus
{
    Pending,
    ValidationFailed,
    Submitted,
    Rejected,
    Open,
    CloseRequested,
    Closed,
    CloseFailed,
    Failed
}

public enum TradeCloseSource
{
    User,
    System,
    TakeProfit,
    StopLoss,
    Platform
}

public enum TradeEntryMode
{
    MarketNow,
    NearRecommendedEntry,
    RejectOnDrift
}

public sealed record TradeExecutionCandidate
{
    public required Guid SignalId { get; init; }
    public Guid? EvaluationId { get; init; }
    public required SignalExecutionSourceType SourceType { get; init; }
    public required string Symbol { get; init; }
    public required string Timeframe { get; init; }
    public required DateTimeOffset SignalTimeUtc { get; init; }
    public required SignalDirection Direction { get; init; }
    public decimal RecommendedEntryPrice { get; init; }
    public decimal TpPrice { get; init; }
    public decimal SlPrice { get; init; }
    public decimal RiskPercent { get; init; }
    public decimal RiskUsd { get; init; }
    public int ConfidenceScore { get; init; }
    public required Regime Regime { get; init; }
    public required IReadOnlyList<string> Reasons { get; init; }
    public string? StrategyVersion { get; init; }
    public string? ExitModel { get; init; }
    public string? ExitExplanation { get; init; }
}

public sealed record TradeExecutionRequest
{
    public required TradeExecutionCandidate Candidate { get; init; }
    public decimal? RequestedSize { get; init; }
    public string RequestedBy { get; init; } = "system";
    public bool ForceMarketExecution { get; init; }
}

public sealed record TradeExecutionResult
{
    public bool Success { get; init; }
    public long? ExecutedTradeId { get; init; }
    public required ExecutedTradeStatus Status { get; init; }
    public string? DealReference { get; init; }
    public string? DealId { get; init; }
    public decimal? ActualEntryPrice { get; init; }
    public decimal? ExecutedSize { get; init; }
    public string? FailureReason { get; init; }
    public string? ErrorDetails { get; init; }
    public string Message { get; init; } = "";
}

public sealed record ForceCloseRequest
{
    public string RequestedBy { get; init; } = "user";
    public string? Reason { get; init; }
}

public sealed record ForceCloseResult
{
    public bool Success { get; init; }
    public required string Message { get; init; }
    public string? DealReference { get; init; }
    public string? DealId { get; init; }
    public decimal? CloseLevel { get; init; }
    public decimal? Pnl { get; init; }
}

public sealed record AccountSnapshot
{
    public long SnapshotId { get; init; }
    public required string AccountId { get; init; }
    public required string AccountName { get; init; }
    public required string Currency { get; init; }
    public decimal Balance { get; init; }
    public decimal Equity { get; init; }
    public decimal Available { get; init; }
    public decimal Margin { get; init; }
    public decimal Funds { get; init; }
    public int OpenPositions { get; init; }
    public bool IsDemo { get; init; }
    public bool HedgingMode { get; init; }
    public required DateTimeOffset CapturedAtUtc { get; init; }
}

public sealed record ExecutedTrade
{
    public long ExecutedTradeId { get; init; }
    public required Guid SignalId { get; init; }
    public Guid? EvaluationId { get; init; }
    public required SignalExecutionSourceType SourceType { get; init; }
    public required string Symbol { get; init; }
    public required string Instrument { get; init; }
    public required string Timeframe { get; init; }
    public required SignalDirection Direction { get; init; }
    public decimal RecommendedEntryPrice { get; init; }
    public decimal ActualEntryPrice { get; init; }
    public decimal TpPrice { get; init; }
    public decimal SlPrice { get; init; }
    public decimal RequestedSize { get; init; }
    public decimal ExecutedSize { get; init; }
    public string? DealReference { get; init; }
    public string? DealId { get; init; }
    public required ExecutedTradeStatus Status { get; init; }
    public required string AccountCurrency { get; init; }
    public decimal? Pnl { get; init; }
    public string? FailureReason { get; init; }
    public string? ErrorDetails { get; init; }
    public bool ForceClosed { get; init; }
    public TradeCloseSource? CloseSource { get; init; }
    public DateTimeOffset? OpenedAtUtc { get; init; }
    public DateTimeOffset? ClosedAtUtc { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record ExecutedTradeQuery
{
    public DateTimeOffset? FromUtc { get; init; }
    public DateTimeOffset? ToUtc { get; init; }
    public string? Instrument { get; init; }
    public SignalDirection? Direction { get; init; }
    public string? Timeframe { get; init; }
    public SignalExecutionSourceType? SourceType { get; init; }
    public ExecutedTradeStatus? Status { get; init; }
    public int Limit { get; init; } = 100;
    public int Offset { get; init; }
}

public sealed record ExecutedTradeStats
{
    public int TotalExecuted { get; init; }
    public int OpenTrades { get; init; }
    public int Wins { get; init; }
    public int Losses { get; init; }
    public int FailedExecutions { get; init; }
    public decimal TotalPnl { get; init; }
    public decimal WinRate { get; init; }
    public required string Currency { get; init; }
}
