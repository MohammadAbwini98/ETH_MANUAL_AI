namespace EthSignal.Domain.Models;

public enum ReplayMode
{
    HistoricalRebuild,
    OptimizationTrain,
    OptimizationVal,
    BackfillOutcomes,
    Diagnostics
}

public enum RunStatus { Queued, Running, Completed, Failed, Cancelled }

public sealed record ReplayRun
{
    public long Id { get; init; }
    public required string Symbol { get; init; }
    public string TimeframeBase { get; init; } = "1m";
    public string TimeframePrimary { get; init; } = "5m";
    public string TimeframeBias { get; init; } = "15m";
    public required DateTimeOffset StartUtc { get; init; }
    public required DateTimeOffset EndUtc { get; init; }
    public long? ParameterSetId { get; init; }
    public required string StrategyVersion { get; init; }
    public ReplayMode Mode { get; init; } = ReplayMode.HistoricalRebuild;
    public RunStatus Status { get; set; } = RunStatus.Queued;
    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? FinishedUtc { get; set; }
    public int CandlesReadCount { get; set; }
    public int SignalsGeneratedCount { get; set; }
    public int OutcomesFinalizedCount { get; set; }
    public int GapEventCount { get; set; }
    public List<string> Warnings { get; set; } = [];
    public string? ErrorText { get; set; }
    public string? CodeVersion { get; set; }
    public string TriggerSource { get; init; } = "manual";
    public DateTimeOffset? CheckpointTime { get; set; }
}
