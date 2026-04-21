using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Engine;

namespace EthSignal.Infrastructure.Trading;

public sealed record TradeExecutionPolicySettings
{
    public bool Enabled { get; init; }
    public bool AutoExecuteEnabled { get; init; }
    public bool DemoOnly { get; init; } = true;
    public int StaleWindowMinutes { get; init; } = 30;
    public decimal EntryDriftTolerancePct { get; init; } = 0.005m;
    public decimal EntryPriceMarginUsd { get; init; } = 1m;
    public TradeEntryMode GeneratedEntryMode { get; init; } = TradeEntryMode.NearRecommendedEntry;
    public decimal GeneratedEntryDriftTolerancePct { get; init; } = 0.005m;
    public decimal GeneratedEntryPriceMarginUsd { get; init; } = 1m;
    public int QueueConcurrentRequestLimit { get; init; } = 3;
    public int MaxConcurrentOpenTrades { get; init; } = 3;
    public TradeEntryMode EntryMode { get; init; } = TradeEntryMode.NearRecommendedEntry;
    public ISet<SignalExecutionSourceType> AllowedSourceTypes { get; init; } =
        new HashSet<SignalExecutionSourceType>
        {
            SignalExecutionSourceType.Recommended
        };
}

public sealed record TradeExecutionPlan
{
    public required TradeExecutionCandidate Candidate { get; init; }
    public required string Epic { get; init; }
    public required string InstrumentName { get; init; }
    public required decimal RequestedSize { get; init; }
    public required decimal FinalSize { get; init; }
    public required decimal RequestedEntryPrice { get; init; }
    public required decimal MarketEntryPrice { get; init; }
    public required decimal ProfitLevel { get; init; }
    public required decimal StopLevel { get; init; }
    public required string Currency { get; init; }
    public required AccountSnapshot AccountSnapshot { get; init; }
    public required string ValidationNote { get; init; }
}

public sealed record TradeExecutionPolicyDecision
{
    public bool Allowed { get; init; }
    public required string Message { get; init; }
    public string? FailureReason { get; init; }
    public TradeExecutionPlan? Plan { get; init; }
}

public sealed record BrokerHealthSnapshot
{
    public bool DemoOnly { get; init; }
    public bool SessionReady { get; init; }
    public bool ExecutionEnabled { get; init; }
    public string? RequiredDemoAccountName { get; init; }
    public string? AccountName { get; init; }
    public string? AccountId { get; init; }
    public bool? ActiveAccountIsDemo { get; init; }
    public bool? ActiveAccountMatchesRequiredDemo { get; init; }
    public DateTimeOffset? LastSyncUtc { get; init; }
    public DateTimeOffset? LatestAccountResolutionUtc { get; init; }
    public string? AccountSelectionSource { get; init; }
    public string? LatestExecutionAccountId { get; init; }
    public string? LatestExecutionAccountName { get; init; }
    public string? LatestBrokerError { get; init; }
    public string? LatestOrderNote { get; init; }
}

public interface ITradeExecutionService
{
    Task<TradeExecutionResult> ExecuteAsync(TradeExecutionRequest request, CancellationToken ct = default);
    Task<ForceCloseResult> ForceCloseAsync(long executedTradeId, ForceCloseRequest request, CancellationToken ct = default);
}

public sealed record TradeExecutionQueueResult
{
    public bool Accepted { get; init; }
    public long? QueueEntryId { get; init; }
    public long? ExecutedTradeId { get; init; }
    public required string Status { get; init; }
    public string? FailureReason { get; init; }
    public required string Message { get; init; }
}

public sealed record TradeExecutionQueueEntrySnapshot
{
    public long QueueEntryId { get; init; }
    public required Guid SignalId { get; init; }
    public Guid? EvaluationId { get; init; }
    public required SignalExecutionSourceType SourceType { get; init; }
    public required string RequestedBy { get; init; }
    public decimal? RequestedSize { get; init; }
    public bool ForceMarketExecution { get; init; }
    public required TradeExecutionQueueStatus Status { get; init; }
    public long? ExecutedTradeId { get; init; }
    public string? FailureReason { get; init; }
    public string? ErrorDetails { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public DateTimeOffset? ProcessedAtUtc { get; init; }
    public double AgeSeconds { get; init; }
    public double WaitSeconds { get; init; }
}

public sealed record TradeExecutionQueueSnapshot
{
    public required DateTimeOffset ServerTimeUtc { get; init; }
    public int ActiveTradeCount { get; init; }
    public int BrokerOpenTradeCount { get; init; }
    public int PendingSubmissionCount { get; init; }
    public int MaxConcurrentOpenTrades { get; init; }
    public int QueueConcurrentRequestLimit { get; init; }
    public int AvailableDispatchSlots { get; init; }
    public int QueuedCount { get; init; }
    public int ProcessingCount { get; init; }
    public int CompletedCount { get; init; }
    public int FailedCount { get; init; }
    public required IReadOnlyList<TradeExecutionQueueEntrySnapshot> Entries { get; init; }
}

public interface ITradeExecutionQueueService
{
    Task<TradeExecutionQueueResult> EnqueueAsync(TradeExecutionRequest request, CancellationToken ct = default);
    Task<int> DrainAsync(CancellationToken ct = default);
    Task<TradeExecutionQueueSnapshot> GetSnapshotAsync(int limit = 50, CancellationToken ct = default);
    Task WaitForWorkAsync(CancellationToken ct = default);
    void NotifyWorkAvailable();
}

public interface IExecutedTradeResetService
{
    Task<ExecutedTradeResetResult> ResetAsync(CancellationToken ct = default);
}

public sealed record ExecutedTradeResetResult
{
    public required DateTimeOffset ResetAtUtc { get; init; }
    public int QueueEntriesCleared { get; init; }
    public int ExecutedTradesCleared { get; init; }
    public int ExecutionAttemptsCleared { get; init; }
    public int ExecutionEventsCleared { get; init; }
    public int AccountSnapshotsCleared { get; init; }
    public int CloseActionsCleared { get; init; }
}

public interface ITradeExecutionPolicy
{
    Task<TradeExecutionPolicyDecision> EvaluateAsync(TradeExecutionRequest request, CancellationToken ct = default);
    TradeExecutionPolicySettings GetSettings();
}

public interface IExecutionCandidateMapper
{
    TradeExecutionCandidate FromRecommended(SignalRecommendation signal);
    TradeExecutionCandidate FromGenerated(GeneratedSignalRecommendation signal);
    TradeExecutionCandidate FromBlocked(BlockedSignalRecommendation signal);
}

public interface IAccountSnapshotService
{
    Task<AccountSnapshot> GetLatestAsync(CancellationToken ct = default);
}

public interface ICapitalTradingClient
{
    bool IsDemoEnvironment { get; }
    Task EnsureDemoReadyAsync(CancellationToken ct = default);
    Task<CapitalMarketInfo> GetMarketInfoAsync(string epic, CancellationToken ct = default);
    Task<CapitalAccountInfo> GetAccountInfoAsync(CancellationToken ct = default);
    Task<CapitalOpenPositionResult> PlacePositionAsync(CapitalPlacePositionRequest request, CancellationToken ct = default);
    Task<CapitalDealConfirmation> ConfirmDealAsync(string dealReference, CancellationToken ct = default);
    Task<IReadOnlyList<CapitalPositionSnapshot>> GetOpenPositionsAsync(CancellationToken ct = default);
    Task<CapitalPositionSnapshot?> GetPositionAsync(string dealId, CancellationToken ct = default);
    Task<IReadOnlyList<CapitalActivityRecord>> GetActivityHistoryAsync(CapitalActivityQuery query, CancellationToken ct = default);
    Task<CapitalClosePositionResult> ClosePositionAsync(CapitalClosePositionRequest request, CancellationToken ct = default);
}

public sealed record CapitalPlacePositionRequest
{
    public required string Epic { get; init; }
    public required SignalDirection Direction { get; init; }
    public required decimal Size { get; init; }
    public decimal? StopLevel { get; init; }
    public decimal? ProfitLevel { get; init; }
}

public sealed record CapitalOpenPositionResult
{
    public required string DealReference { get; init; }
    public string? Note { get; init; }
}

public sealed record CapitalClosePositionRequest
{
    public required string DealId { get; init; }
}

public sealed record CapitalClosePositionResult
{
    public required string DealReference { get; init; }
    public string? Note { get; init; }
}

public sealed record CapitalDealConfirmation
{
    public required string DealReference { get; init; }
    public string? DealId { get; init; }
    public string? Status { get; init; }
    public string? DealStatus { get; init; }
    public string? Epic { get; init; }
    public decimal? Level { get; init; }
    public decimal? Size { get; init; }
    public SignalDirection? Direction { get; init; }
    public required bool Accepted { get; init; }
    public string? RejectionReason { get; init; }
    public string? Note { get; init; }
}

public sealed record CapitalMarketInfo
{
    public required string Epic { get; init; }
    public required string Symbol { get; init; }
    public required string InstrumentName { get; init; }
    public required string Currency { get; init; }
    public required bool Tradeable { get; init; }
    public decimal Bid { get; init; }
    public decimal Offer { get; init; }
    public int DecimalPlaces { get; init; }
    public decimal MinDealSize { get; init; }
    public decimal MinSizeIncrement { get; init; }
    public decimal MinStopOrProfitDistance { get; init; }
    public string MinStopOrProfitDistanceUnit { get; init; } = "";
    public decimal MarginFactor { get; init; }
    public string MarginFactorUnit { get; init; } = "";
}

public sealed record CapitalAccountInfo
{
    public required string AccountId { get; init; }
    public required string AccountName { get; init; }
    public required string Currency { get; init; }
    public decimal Balance { get; init; }
    public decimal Available { get; init; }
    public decimal ProfitLoss { get; init; }
    public decimal Equity { get; init; }
    public bool HedgingMode { get; init; }
    public bool IsDemo { get; init; }
    public string ResolutionSource { get; init; } = "";
    public DateTimeOffset? ResolvedAtUtc { get; init; }
}

public sealed record CapitalPositionSnapshot
{
    public required string DealId { get; init; }
    public string? DealReference { get; init; }
    public required string Epic { get; init; }
    public required SignalDirection Direction { get; init; }
    public decimal Size { get; init; }
    public decimal Level { get; init; }
    public decimal? StopLevel { get; init; }
    public decimal? ProfitLevel { get; init; }
    public string Currency { get; init; } = "";
}

public sealed record CapitalActivityQuery
{
    public DateTimeOffset? FromUtc { get; init; }
    public DateTimeOffset? ToUtc { get; init; }
    public int? LastPeriodSeconds { get; init; }
    public string? DealId { get; init; }
    public bool Detailed { get; init; } = true;
}

public sealed record CapitalActivityRecord
{
    public required DateTimeOffset DateUtc { get; init; }
    public string? Epic { get; init; }
    public string? DealId { get; init; }
    public string? Source { get; init; }
    public string? Type { get; init; }
    public string? Status { get; init; }
    public decimal? Level { get; init; }
    public decimal? Size { get; init; }
    public string? Currency { get; init; }
    public string? DetailsJson { get; init; }
}
