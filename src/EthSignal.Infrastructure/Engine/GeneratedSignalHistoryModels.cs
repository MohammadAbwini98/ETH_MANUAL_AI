using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Engine;

public sealed record GeneratedSignalRecommendation
{
    public required Guid SignalId { get; init; }
    public Guid EvaluationId { get; init; }
    public required string Symbol { get; init; }
    public required string Timeframe { get; init; }
    public required DateTimeOffset SignalTimeUtc { get; init; }
    public required DateTimeOffset DecisionTimeUtc { get; init; }
    public required DateTimeOffset BarTimeUtc { get; init; }
    public required SignalDirection Direction { get; init; }
    public required SignalLifecycleState LifecycleState { get; init; }
    public required DecisionOrigin Origin { get; init; }
    public required SourceMode SourceMode { get; init; }
    public required Regime Regime { get; init; }
    public required string StrategyVersion { get; init; }
    public required IReadOnlyList<string> Reasons { get; init; }
    public decimal EntryPrice { get; init; }
    public decimal TpPrice { get; init; }
    public decimal SlPrice { get; init; }
    public decimal RiskPercent { get; init; }
    public decimal RiskUsd { get; init; }
    public int ConfidenceScore { get; init; }
    public int ExpiryBars { get; init; }
    public DateTimeOffset ExpiryTimeUtc { get; init; }
    public string? ExitModel { get; init; }
    public string? ExitExplanation { get; init; }
    public bool UsedFallbackExit { get; init; }
}

public sealed record GeneratedSignalWithOutcome
{
    public required GeneratedSignalRecommendation Signal { get; init; }
    public required SignalOutcome Outcome { get; init; }
}

public sealed record GeneratedSignalHistoryPage
{
    public required IReadOnlyList<GeneratedSignalWithOutcome> Signals { get; init; }
    public required PerformanceStats Stats { get; init; }
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}

public interface IGeneratedSignalHistoryService
{
    Task<GeneratedSignalHistoryPage> GetHistoryAsync(
        string symbol,
        int pageSize,
        int offset,
        int? hours = null,
        CancellationToken ct = default);
    Task<GeneratedSignalWithOutcome?> GetBySignalIdAsync(
        string symbol,
        Guid signalId,
        CancellationToken ct = default);
}
