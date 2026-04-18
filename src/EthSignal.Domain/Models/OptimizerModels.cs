namespace EthSignal.Domain.Models;

public sealed record OptimizerRun
{
    public long Id { get; init; }
    public required string Symbol { get; init; }
    public required string StrategyVersion { get; init; }
    public long? BaselineParameterSetId { get; init; }
    public string? SearchSpaceJson { get; init; }
    public string? ObjectiveFunctionVersion { get; init; }
    public required DateTimeOffset StartUtc { get; init; }
    public required DateTimeOffset EndUtc { get; init; }
    public RunStatus Status { get; set; } = RunStatus.Queued;
    public string RunMode { get; init; } = "manual";
    public int FoldCount { get; init; } = 3;
    public int CandidateCount { get; set; }
    public long? BestCandidateId { get; set; }
    public decimal? BestScore { get; set; }
    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? FinishedUtc { get; set; }
    public string? SummaryJson { get; set; }
    public string? ErrorText { get; set; }
}

public sealed record OptimizerCandidate
{
    public long Id { get; init; }
    public long OptimizerRunId { get; init; }
    public long ParameterSetId { get; init; }
    public string Status { get; set; } = "pending";
    public decimal? TrainScore { get; set; }
    public decimal? ValidationScore { get; set; }
    public decimal? BaselineDeltaPct { get; set; }
    public int? TradeCount { get; set; }
    public decimal? WinRate { get; set; }
    public decimal? ExpectancyR { get; set; }
    public decimal? TotalPnlR { get; set; }
    public decimal? ProfitFactor { get; set; }
    public decimal? MaxDrawdownR { get; set; }
    public decimal? TimeoutRate { get; set; }
    public decimal? OverfitPenalty { get; set; }
    public decimal? SparsityPenalty { get; set; }
    public int? Rank { get; set; }
}

public sealed record OptimizerCandidateFold
{
    public long Id { get; init; }
    public long OptimizerCandidateId { get; init; }
    public int FoldIndex { get; init; }
    public required DateTimeOffset TrainStartUtc { get; init; }
    public required DateTimeOffset TrainEndUtc { get; init; }
    public required DateTimeOffset ValStartUtc { get; init; }
    public required DateTimeOffset ValEndUtc { get; init; }
    public string? TrainMetricsJson { get; set; }
    public string? ValMetricsJson { get; set; }
    public string? WarningsJson { get; set; }
}

/// <summary>Metrics computed per candidate per fold.</summary>
public sealed record ReplayMetrics
{
    public int TradeCount { get; init; }
    public int Wins { get; init; }
    public int Losses { get; init; }
    public int Expired { get; init; }
    public int Ambiguous { get; init; }
    public int Pending { get; init; }
    public decimal WinRate { get; init; }
    public decimal AvgPnlR { get; init; }
    public decimal TotalPnlR { get; init; }
    public decimal ProfitFactor { get; init; }
    public decimal MaxDrawdownR { get; init; }
    public decimal ExpectancyR { get; init; }
    public decimal TimeoutRate { get; init; }
    public decimal NoTradeRate { get; init; }
    public decimal SignalDensity { get; init; }
}
