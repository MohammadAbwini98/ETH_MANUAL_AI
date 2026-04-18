using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Db;

/// <summary>TF-2: Repository for persisting all signal evaluation outcomes (including NO_TRADE).</summary>
public interface IDecisionAuditRepository
{
    /// <summary>Persist a decision. Returns true if inserted, false if duplicate (safe ignore per NFR-3).</summary>
    Task<bool> InsertDecisionAsync(SignalDecision decision, CancellationToken ct = default);

    /// <summary>Check whether a decision already exists for the given bar (prevents duplicates for warm-start).</summary>
    Task<bool> ExistsForBarAsync(string symbol, string timeframe, DateTimeOffset barTimeUtc, SourceMode sourceMode, CancellationToken ct = default);

    /// <summary>Get the latest decision for a symbol.</summary>
    Task<SignalDecision?> GetLatestDecisionAsync(string symbol, CancellationToken ct = default);

    /// <summary>Get decisions in a time range for export/dashboard.</summary>
    Task<IReadOnlyList<SignalDecision>> GetDecisionsAsync(string symbol, DateTimeOffset from, DateTimeOffset to, int limit = 1000, CancellationToken ct = default);

    /// <summary>Get summary counts for dashboard.</summary>
    Task<DecisionSummary> GetSummaryAsync(string symbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);

    /// <summary>
    /// Historical blocked decisions that have outcomes but are still missing ML feature snapshots.
    /// Used for one-time training-data backfills.
    /// </summary>
    Task<IReadOnlyList<DecisionMlBackfillCandidate>> GetBlockedMlBackfillCandidatesAsync(
        string symbol,
        CancellationToken ct = default);
}

/// <summary>TF-7: Decision count summary for dashboard.</summary>
public sealed record DecisionSummary
{
    public int TotalDecisions { get; init; }
    public int LongCount { get; init; }
    public int ShortCount { get; init; }
    public int NoTradeCount { get; init; }
    public int StrategyNoTradeCount { get; init; }
    public int OperationalBlockedCount { get; init; }
    public int ContextNotReadyCount { get; init; }
    public DateTimeOffset? LastSignalTime { get; init; }
    public DateTimeOffset? LastEvaluationTime { get; init; }
    public List<(string Reason, int Count)> TopRejectReasons { get; init; } = new();
}

public sealed record DecisionMlBackfillCandidate
{
    public required Guid EvaluationId { get; init; }
    public required string Symbol { get; init; }
    public required string Timeframe { get; init; }
    public required DateTimeOffset SignalTimeUtc { get; init; }
    public required DateTimeOffset DecisionTimeUtc { get; init; }
    public required DateTimeOffset BarTimeUtc { get; init; }
    public required string DecisionTypeRaw { get; init; }
    public string? CandidateDirectionRaw { get; init; }
    public string? RegimeRaw { get; init; }
    public int ConfidenceScore { get; init; }
    public string? ParameterSetId { get; init; }
    public string? EffectiveRuntimeParametersJson { get; init; }
    public required string IndicatorsJson { get; init; }
}
