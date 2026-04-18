using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EthSignal.Infrastructure.Engine;

/// <summary>
/// B-07 Phase 4: Optimizer core.
/// Evaluates candidate parameter sets against replay results using walk-forward validation.
/// </summary>
public sealed class OptimizerService
{
    private readonly HistoricalReplayService _replay;
    private readonly IParameterRepository _paramRepo;
    private readonly IReplayRepository _replayRepo;
    private readonly ILogger<OptimizerService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public OptimizerService(
        HistoricalReplayService replay,
        IParameterRepository paramRepo,
        IReplayRepository replayRepo,
        ILogger<OptimizerService>? logger = null)
    {
        _replay = replay;
        _paramRepo = paramRepo;
        _replayRepo = replayRepo;
        _logger = logger ?? NullLogger<OptimizerService>.Instance;
    }

    /// <summary>
    /// Run a full optimization: generate candidates, walk-forward evaluate, rank, return best.
    /// </summary>
    public async Task<OptimizerRunResult> RunAsync(
        string symbol,
        DateTimeOffset dataStartUtc,
        DateTimeOffset dataEndUtc,
        StrategyParameters baseline,
        OptimizerConfig config,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Optimizer starting: {Symbol} {Start}-{End}, folds={Folds}, candidates={Max}",
            symbol, dataStartUtc, dataEndUtc, config.FoldCount, config.MaxCandidates);

        var result = new OptimizerRunResult { Baseline = baseline };

        // 1) Evaluate baseline across all folds
        var folds = GenerateFolds(dataStartUtc, dataEndUtc, config.FoldCount);
        result.BaselineMetrics = await EvaluateAcrossFolds(symbol, baseline, folds, ct);

        _logger.LogInformation("Baseline score: {Score:F4} ({Trades} trades, WR={WR:P1})",
            result.BaselineMetrics.Score, result.BaselineMetrics.Metrics.TradeCount,
            result.BaselineMetrics.Metrics.WinRate);

        // 2) Generate candidate parameter sets
        var candidates = GenerateCandidates(baseline, config);
        _logger.LogInformation("Generated {Count} valid candidates", candidates.Count);

        // 3) Evaluate each candidate
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var eval = await EvaluateAcrossFolds(symbol, candidate, folds, ct);

            // Hard filters
            string? rejection = ApplyHardFilters(eval.Metrics, config);
            if (rejection != null)
            {
                result.RejectedCandidates.Add(new CandidateResult
                {
                    Parameters = candidate,
                    Evaluation = eval,
                    RejectionReason = rejection
                });
                continue;
            }

            // Compute overfit penalty and final score
            eval.OverfitPenalty = Math.Max(0, eval.TrainScore - eval.Score);
            decimal sparsityPen = eval.Metrics.TradeCount < config.MinTradeCount
                ? (decimal)(config.MinTradeCount - eval.Metrics.TradeCount) / config.MinTradeCount
                : 0;
            eval.FinalScore = eval.Score - 0.03m * eval.OverfitPenalty - 0.02m * sparsityPen;

            decimal deltaVsBaseline = result.BaselineMetrics.Score > 0
                ? (eval.FinalScore - result.BaselineMetrics.Score) / Math.Abs(result.BaselineMetrics.Score) * 100m
                : eval.FinalScore > 0 ? 100m : 0;

            result.EvaluatedCandidates.Add(new CandidateResult
            {
                Parameters = candidate,
                Evaluation = eval,
                BaselineDeltaPct = deltaVsBaseline
            });

            _logger.LogDebug("Candidate score={Score:F4} delta={Delta:F1}% trades={Trades}",
                eval.FinalScore, deltaVsBaseline, eval.Metrics.TradeCount);
        }

        // 4) Rank by final score descending
        result.EvaluatedCandidates = result.EvaluatedCandidates
            .OrderByDescending(c => c.Evaluation.FinalScore)
            .ToList();

        for (int i = 0; i < result.EvaluatedCandidates.Count; i++)
            result.EvaluatedCandidates[i].Rank = i + 1;

        // 5) Determine best promotable candidate
        var best = result.EvaluatedCandidates.FirstOrDefault(c =>
            c.BaselineDeltaPct >= config.MinImprovementPct
            && c.Evaluation.Metrics.TradeCount >= config.MinTradeCount
            && c.Evaluation.Metrics.MaxDrawdownR <= result.BaselineMetrics.Metrics.MaxDrawdownR * config.MaxDrawdownExpansion);

        result.BestCandidate = best;

        _logger.LogInformation("Optimizer done: {Evaluated} evaluated, {Rejected} rejected, best={BestRank}",
            result.EvaluatedCandidates.Count, result.RejectedCandidates.Count,
            best?.Rank.ToString() ?? "none");

        return result;
    }

    /// <summary>Persist best candidate as a draft parameter set in the DB.</summary>
    public async Task<long?> PersistBestCandidateAsync(OptimizerRunResult result, string? createdBy = null, CancellationToken ct = default)
    {
        if (result.BestCandidate == null) return null;

        var p = result.BestCandidate.Parameters;
        var hash = ComputeHash(p.ToJson());

        var set = new StrategyParameterSet
        {
            StrategyVersion = p.StrategyVersion,
            ParameterHash = hash,
            Parameters = p,
            Status = ParameterSetStatus.Candidate,
            CreatedBy = createdBy ?? "optimizer",
            ObjectiveFunctionVersion = p.ObjectiveFunctionVersion,
            Notes = $"Score={result.BestCandidate.Evaluation.FinalScore:F4}, " +
                    $"Delta={result.BestCandidate.BaselineDeltaPct:F1}%"
        };

        return await _paramRepo.InsertAsync(set, ct);
    }

    // ─── Walk-Forward ────────────────────────────────────

    private List<(DateTimeOffset TrainStart, DateTimeOffset TrainEnd,
        DateTimeOffset ValStart, DateTimeOffset ValEnd)> GenerateFolds(
        DateTimeOffset start, DateTimeOffset end, int foldCount)
    {
        var totalDays = (end - start).TotalDays;
        var valDays = totalDays / (foldCount + 1); // validation window size
        var folds = new List<(DateTimeOffset, DateTimeOffset, DateTimeOffset, DateTimeOffset)>();

        for (int k = 0; k < foldCount; k++)
        {
            var valStart = start.AddDays(valDays * (k + 1));
            var valEnd = valStart.AddDays(valDays);
            if (valEnd > end) valEnd = end;

            var trainStart = start;
            var trainEnd = valStart;

            folds.Add((trainStart, trainEnd, valStart, valEnd));
        }

        return folds;
    }

    private async Task<CandidateEvaluation> EvaluateAcrossFolds(
        string symbol, StrategyParameters parameters,
        List<(DateTimeOffset TrainStart, DateTimeOffset TrainEnd,
            DateTimeOffset ValStart, DateTimeOffset ValEnd)> folds,
        CancellationToken ct)
    {
        var valMetrics = new List<ReplayMetrics>();
        var trainMetrics = new List<ReplayMetrics>();
        decimal trainScoreSum = 0, valScoreSum = 0;

        foreach (var (trainStart, trainEnd, valStart, valEnd) in folds)
        {
            ct.ThrowIfCancellationRequested();

            // Train replay (for overfit detection)
            var trainResult = await _replay.RunInMemoryAsync(symbol, trainStart, trainEnd, parameters, ct);
            var trainM = trainResult.ComputeMetrics();
            trainMetrics.Add(trainM);
            trainScoreSum += ComputeScore(trainM);

            // Validation replay (the real evaluation)
            var valResult = await _replay.RunInMemoryAsync(symbol, valStart, valEnd, parameters, ct);
            var valM = valResult.ComputeMetrics();
            valMetrics.Add(valM);
            valScoreSum += ComputeScore(valM);
        }

        // Aggregate validation metrics
        var avgVal = AggregateMetrics(valMetrics);
        decimal avgTrainScore = folds.Count > 0 ? trainScoreSum / folds.Count : 0;
        decimal avgValScore = folds.Count > 0 ? valScoreSum / folds.Count : 0;

        return new CandidateEvaluation
        {
            Metrics = avgVal,
            Score = avgValScore,
            TrainScore = avgTrainScore,
            FoldMetrics = valMetrics
        };
    }

    // ─── Scoring ─────────────────────────────────────────

    private static decimal ComputeScore(ReplayMetrics m)
    {
        if (m.TradeCount == 0) return -999m;

        // Composite score per SRS §11.6
        return 0.30m * m.TotalPnlR
             + 0.20m * m.ExpectancyR
             + 0.20m * Math.Min(m.ProfitFactor, 5m) // cap PF to avoid outlier dominance
             + 0.10m * m.WinRate
             - 0.10m * m.MaxDrawdownR
             - 0.05m * m.TimeoutRate;
    }

    private static ReplayMetrics AggregateMetrics(List<ReplayMetrics> metrics)
    {
        if (metrics.Count == 0) return new ReplayMetrics();

        return new ReplayMetrics
        {
            TradeCount = (int)metrics.Average(m => m.TradeCount),
            Wins = (int)metrics.Average(m => m.Wins),
            Losses = (int)metrics.Average(m => m.Losses),
            Expired = (int)metrics.Average(m => m.Expired),
            Pending = (int)metrics.Average(m => m.Pending),
            WinRate = metrics.Average(m => m.WinRate),
            AvgPnlR = metrics.Average(m => m.AvgPnlR),
            TotalPnlR = metrics.Average(m => m.TotalPnlR),
            ProfitFactor = metrics.Average(m => m.ProfitFactor),
            MaxDrawdownR = metrics.Max(m => m.MaxDrawdownR),
            ExpectancyR = metrics.Average(m => m.ExpectancyR),
            TimeoutRate = metrics.Average(m => m.TimeoutRate),
            NoTradeRate = metrics.Average(m => m.NoTradeRate),
            SignalDensity = metrics.Average(m => m.SignalDensity)
        };
    }

    // ─── Candidate Generation ────────────────────────────

    private static List<StrategyParameters> GenerateCandidates(StrategyParameters baseline, OptimizerConfig config)
    {
        var rng = new Random(config.Seed ?? 42);
        var candidates = new List<StrategyParameters>();

        var adxValues = new[] { 15m, 18m, 20m, 22m, 25m };
        var volumeValues = new[] { 0.8m, 1.0m, 1.1m, 1.2m, 1.3m, 1.5m };
        var stopAtrValues = new[] { 0.6m, 0.8m, 1.0m, 1.2m };
        var targetRValues = new[] { 1.0m, 1.25m, 1.5m, 2.0m };
        var rsiBuyMinValues = new[] { 35m, 40m, 45m };
        var rsiBuyMaxValues = new[] { 60m, 65m, 70m };
        var confBuyValues = new[] { 55, 60, 65, 70, 75 };
        var structureLookback = new[] { 3, 4, 5, 7 };
        var spreadValues = new[] { 0.003m, 0.005m, 0.008m };

        int attempts = 0;
        int maxAttempts = config.MaxCandidates * 5;

        while (candidates.Count < config.MaxCandidates && attempts < maxAttempts)
        {
            attempts++;

            var candidate = baseline with
            {
                AdxTrendThreshold = Pick(adxValues, rng),
                VolumeMultiplierMin = Pick(volumeValues, rng),
                StopAtrMultiplier = Pick(stopAtrValues, rng),
                TargetRMultiple = Pick(targetRValues, rng),
                RsiBuyMin = Pick(rsiBuyMinValues, rng),
                RsiBuyMax = Pick(rsiBuyMaxValues, rng),
                ConfidenceBuyThreshold = Pick(confBuyValues, rng),
                ConfidenceSellThreshold = Pick(confBuyValues, rng),
                MarketStructureLookback = Pick(structureLookback, rng),
                MaxSpreadPct = Pick(spreadValues, rng)
            };

            // Validate constraints
            if (candidate.Validate() != null) continue;
            if (candidate.RsiBuyMin >= candidate.RsiBuyMax) continue;

            // Deduplicate
            var hash = candidate.ToJson();
            if (candidates.Any(c => c.ToJson() == hash)) continue;

            candidates.Add(candidate);
        }

        return candidates;
    }

    private static T Pick<T>(T[] values, Random rng) => values[rng.Next(values.Length)];

    private static string? ApplyHardFilters(ReplayMetrics m, OptimizerConfig config)
    {
        if (m.TradeCount < config.MinTradeCount)
            return $"TradeCount({m.TradeCount}) < min({config.MinTradeCount})";
        if (m.ProfitFactor < 1.05m)
            return $"ProfitFactor({m.ProfitFactor:F2}) < 1.05";
        if (m.ExpectancyR <= 0)
            return $"ExpectancyR({m.ExpectancyR:F4}) <= 0";
        if (m.MaxDrawdownR > config.MaxDrawdownLimit)
            return $"MaxDrawdownR({m.MaxDrawdownR:F2}) > limit({config.MaxDrawdownLimit})";
        return null;
    }

    private static string ComputeHash(string json)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}

// ─── Supporting Types ────────────────────────────────

public sealed record OptimizerConfig
{
    public int FoldCount { get; init; } = 3;
    public int MaxCandidates { get; init; } = 50;
    public int MinTradeCount { get; init; } = 10;
    public decimal MinImprovementPct { get; init; } = 5.0m;
    public decimal MaxDrawdownExpansion { get; init; } = 1.10m;
    public decimal MaxDrawdownLimit { get; init; } = 10m;
    public int? Seed { get; init; } = 42;
}

public sealed class OptimizerRunResult
{
    public StrategyParameters Baseline { get; set; } = StrategyParameters.Default;
    public CandidateEvaluation BaselineMetrics { get; set; } = new();
    public List<CandidateResult> EvaluatedCandidates { get; set; } = [];
    public List<CandidateResult> RejectedCandidates { get; } = [];
    public CandidateResult? BestCandidate { get; set; }
}

public sealed class CandidateEvaluation
{
    public ReplayMetrics Metrics { get; init; } = new();
    public decimal Score { get; init; }
    public decimal TrainScore { get; init; }
    public decimal OverfitPenalty { get; set; }
    public decimal FinalScore { get; set; }
    public List<ReplayMetrics> FoldMetrics { get; init; } = [];
}

public sealed class CandidateResult
{
    public StrategyParameters Parameters { get; init; } = StrategyParameters.Default;
    public CandidateEvaluation Evaluation { get; init; } = new();
    public decimal BaselineDeltaPct { get; set; }
    public int Rank { get; set; }
    public string? RejectionReason { get; set; }
}
