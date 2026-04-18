using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using Microsoft.Extensions.Logging;

namespace EthSignal.Infrastructure.Engine.ML;

/// <summary>
/// Continuously adjusts StrategyParameters based on rolling performance windows.
/// Only tunes safe parameters (never risk management). Safety guards prevent oscillation.
/// </summary>
public sealed class AdaptiveParameterTuner
{
    private readonly IParameterRepository _paramRepo;
    private readonly ISignalRepository _signalRepo;
    private readonly ILogger<AdaptiveParameterTuner> _logger;

    // Rolling window of recent outcomes for tuning decisions
    private readonly Queue<SignalOutcome> _recentOutcomes = new();
    private readonly object _lock = new();
    private int _adjustmentCount;
    private int _signalsSinceLastAdjustment;
    private decimal _baselineExpectancy;

    /// <summary>Adjustment frequency: evaluate every N resolved signals.</summary>
    private const int AdjustmentInterval = 25;

    /// <summary>Maximum adaptive adjustments before requiring full optimizer run.</summary>
    private const int MaxAdjustmentsBeforeOptimizer = 3;

    /// <summary>Rolling window size for performance assessment.</summary>
    private const int WindowSize = 50;

    public AdaptiveParameterTuner(
        IParameterRepository paramRepo,
        ISignalRepository signalRepo,
        ILogger<AdaptiveParameterTuner> logger)
    {
        _paramRepo = paramRepo;
        _signalRepo = signalRepo;
        _logger = logger;
    }

    /// <summary>Set the baseline expectancy from the initial parameter set.</summary>
    public void SetBaseline(decimal baselineExpectancy)
    {
        _baselineExpectancy = baselineExpectancy;
    }

    /// <summary>Record a new resolved outcome. May trigger parameter adjustment.</summary>
    public AdaptiveTuneResult? RecordOutcome(SignalOutcome outcome, StrategyParameters currentParams)
    {
        lock (_lock)
        {
            if (outcome.OutcomeLabel is not (OutcomeLabel.WIN or OutcomeLabel.LOSS))
                return null;

            _recentOutcomes.Enqueue(outcome);
            while (_recentOutcomes.Count > WindowSize)
                _recentOutcomes.Dequeue();

            _signalsSinceLastAdjustment++;

            if (_signalsSinceLastAdjustment < AdjustmentInterval)
                return null;

            if (_adjustmentCount >= MaxAdjustmentsBeforeOptimizer)
            {
                _logger.LogInformation("Adaptive tuner: max {Max} adjustments reached — awaiting optimizer",
                    MaxAdjustmentsBeforeOptimizer);
                return null;
            }

            if (_recentOutcomes.Count < 20)
                return null;

            return EvaluateAndAdjust(currentParams);
        }
    }

    /// <summary>Reset adjustment counter (called after optimizer completes).</summary>
    public void ResetAdjustmentCount()
    {
        lock (_lock)
        {
            _adjustmentCount = 0;
        }
    }

    private AdaptiveTuneResult? EvaluateAndAdjust(StrategyParameters p)
    {
        var outcomes = _recentOutcomes.ToArray();
        int wins = outcomes.Count(o => o.OutcomeLabel == OutcomeLabel.WIN);
        int losses = outcomes.Count(o => o.OutcomeLabel == OutcomeLabel.LOSS);
        int resolved = wins + losses;
        if (resolved < 10) return null;

        decimal winRate = (decimal)wins / resolved;
        decimal avgWinR = resolved > 0
            ? outcomes.Where(o => o.OutcomeLabel == OutcomeLabel.WIN).Select(o => o.PnlR).DefaultIfEmpty(0).Average()
            : 0;
        decimal avgLossR = resolved > 0
            ? outcomes.Where(o => o.OutcomeLabel == OutcomeLabel.LOSS).Select(o => Math.Abs(o.PnlR)).DefaultIfEmpty(0).Average()
            : 0;
        decimal expectancy = winRate * avgWinR - (1m - winRate) * avgLossR;

        // Safety guard G2: emergency revert if expectancy is very negative
        var rolling30 = outcomes.TakeLast(30).ToArray();
        int r30w = rolling30.Count(o => o.OutcomeLabel == OutcomeLabel.WIN);
        int r30l = rolling30.Count(o => o.OutcomeLabel == OutcomeLabel.LOSS);
        int r30 = r30w + r30l;
        if (r30 >= 10)
        {
            decimal r30WinRate = (decimal)r30w / r30;
            decimal r30AvgWin = rolling30.Where(o => o.OutcomeLabel == OutcomeLabel.WIN)
                .Select(o => o.PnlR).DefaultIfEmpty(0).Average();
            decimal r30AvgLoss = rolling30.Where(o => o.OutcomeLabel == OutcomeLabel.LOSS)
                .Select(o => Math.Abs(o.PnlR)).DefaultIfEmpty(0).Average();
            decimal r30Exp = r30WinRate * r30AvgWin - (1m - r30WinRate) * r30AvgLoss;
            if (r30Exp < -0.1m)
            {
                _logger.LogWarning("Adaptive tuner: rolling 30 expectancy {Exp:F4} < -0.1R — REVERT needed", r30Exp);
                return new AdaptiveTuneResult
                {
                    Action = TuneAction.Revert,
                    Reason = $"Rolling 30-signal expectancy {r30Exp:F4}R < -0.1R safety threshold",
                    CurrentExpectancy = expectancy,
                    BaselineExpectancy = _baselineExpectancy
                };
            }
        }

        _signalsSinceLastAdjustment = 0;

        // Determine adjustment direction
        if (_baselineExpectancy == 0) _baselineExpectancy = expectancy;

        decimal ratio = _baselineExpectancy != 0 ? expectancy / _baselineExpectancy : 1m;

        StrategyParameters? adjusted = null;
        string reason;

        if (ratio < 0.80m)
        {
            // Performance degrading: raise thresholds for quality
            adjusted = p with
            {
                ConfidenceBuyThreshold = Math.Min(90, p.ConfidenceBuyThreshold + 5),
                ConfidenceSellThreshold = Math.Min(90, p.ConfidenceSellThreshold + 5)
            };
            reason = $"Expectancy {expectancy:F4}R < 80% of baseline {_baselineExpectancy:F4}R → raising thresholds";
        }
        else if (ratio > 1.20m)
        {
            // Strong performance: lower thresholds for frequency
            adjusted = p with
            {
                ConfidenceBuyThreshold = Math.Max(40, p.ConfidenceBuyThreshold - 3),
                ConfidenceSellThreshold = Math.Max(40, p.ConfidenceSellThreshold - 3),
                PullbackZonePct = Math.Min(0.010m, p.PullbackZonePct + 0.001m)
            };
            reason = $"Expectancy {expectancy:F4}R > 120% of baseline {_baselineExpectancy:F4}R → lowering thresholds";
        }
        else
        {
            // Within normal range — no adjustment
            return null;
        }

        _adjustmentCount++;
        _logger.LogInformation("Adaptive tuner adjustment #{Count}: {Reason}", _adjustmentCount, reason);

        return new AdaptiveTuneResult
        {
            Action = TuneAction.Adjust,
            Reason = reason,
            AdjustedParameters = adjusted,
            CurrentExpectancy = expectancy,
            BaselineExpectancy = _baselineExpectancy,
            AdjustmentNumber = _adjustmentCount
        };
    }
}

public enum TuneAction { Adjust, Revert }

public sealed record AdaptiveTuneResult
{
    public required TuneAction Action { get; init; }
    public required string Reason { get; init; }
    public StrategyParameters? AdjustedParameters { get; init; }
    public decimal CurrentExpectancy { get; init; }
    public decimal BaselineExpectancy { get; init; }
    public int AdjustmentNumber { get; init; }
}
