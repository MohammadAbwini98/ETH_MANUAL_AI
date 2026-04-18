using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using Microsoft.Extensions.Logging;

namespace EthSignal.Infrastructure.Engine.ML;

/// <summary>
/// Monitors ML model predictions for concept drift.
/// Compares rolling metrics against thresholds and triggers retraining/fallback.
/// </summary>
public sealed class MlDriftDetector
{
    private readonly IMlModelRepository _modelRepo;
    private readonly ILogger<MlDriftDetector> _logger;

    // Rolling prediction tracking
    private readonly Queue<(decimal predictedWin, bool actualWin)> _rollingPredictions = new();
    private readonly object _lock = new();

    public MlDriftDetector(IMlModelRepository modelRepo, ILogger<MlDriftDetector> logger)
    {
        _modelRepo = modelRepo;
        _logger = logger;
    }

    /// <summary>Record a prediction outcome for drift monitoring.</summary>
    public void RecordOutcome(decimal predictedWinProb, bool actualWin)
    {
        lock (_lock)
        {
            _rollingPredictions.Enqueue((predictedWinProb, actualWin));
            // Keep last 100 predictions
            while (_rollingPredictions.Count > 100)
                _rollingPredictions.Dequeue();
        }
    }

    /// <summary>
    /// Check for concept drift. Returns a drift event if detected, null otherwise.
    /// </summary>
    public DriftCheckResult CheckDrift(StrategyParameters p, string modelVersion)
    {
        lock (_lock)
        {
            if (_rollingPredictions.Count < 30)
                return new DriftCheckResult { DriftDetected = false };

            var items = _rollingPredictions.ToArray();

            // Rolling AUC-ROC approximation (concordance ratio)
            decimal auc = ComputeApproxAuc(items);
            bool aucDrift = auc < p.MlDriftAucThreshold;

            // Rolling Brier Score
            decimal brier = ComputeBrierScore(items);
            bool brierDrift = brier > p.MlDriftBrierThreshold;

            // Win rate vs predicted comparison
            decimal actualWinRate = (decimal)items.Count(x => x.actualWin) / items.Length;
            decimal predictedMean = items.Average(x => x.predictedWin);
            decimal winRateGap = Math.Abs(actualWinRate - predictedMean);
            bool winRateDrift = winRateGap > 0.15m;

            bool driftDetected = aucDrift || brierDrift || winRateDrift;

            if (driftDetected)
            {
                var metrics = new List<string>();
                if (aucDrift) metrics.Add($"AUC={auc:F4} < {p.MlDriftAucThreshold}");
                if (brierDrift) metrics.Add($"Brier={brier:F4} > {p.MlDriftBrierThreshold}");
                if (winRateDrift) metrics.Add($"WinRateGap={winRateGap:F4} > 0.15");

                _logger.LogWarning("ML drift detected for {Model}: {Metrics}",
                    modelVersion, string.Join(", ", metrics));
            }

            return new DriftCheckResult
            {
                DriftDetected = driftDetected,
                RollingAuc = auc,
                RollingBrier = brier,
                ActualWinRate = actualWinRate,
                PredictedMeanWin = predictedMean,
                WindowSize = items.Length,
                AucDrift = aucDrift,
                BrierDrift = brierDrift,
                WinRateDrift = winRateDrift
            };
        }
    }

    /// <summary>Approximate AUC as concordance ratio.</summary>
    private static decimal ComputeApproxAuc(ReadOnlySpan<(decimal predictedWin, bool actualWin)> items)
    {
        int concordant = 0, discordant = 0;
        for (int i = 0; i < items.Length; i++)
        {
            if (!items[i].actualWin) continue;
            for (int j = 0; j < items.Length; j++)
            {
                if (items[j].actualWin) continue;
                if (items[i].predictedWin > items[j].predictedWin) concordant++;
                else if (items[i].predictedWin < items[j].predictedWin) discordant++;
            }
        }
        int total = concordant + discordant;
        return total > 0 ? (decimal)concordant / total : 0.5m;
    }

    private static decimal ComputeBrierScore(ReadOnlySpan<(decimal predictedWin, bool actualWin)> items)
    {
        decimal sum = 0;
        for (int i = 0; i < items.Length; i++)
        {
            decimal actual = items[i].actualWin ? 1m : 0m;
            decimal diff = items[i].predictedWin - actual;
            sum += diff * diff;
        }
        return items.Length > 0 ? sum / items.Length : 0;
    }
}

public sealed record DriftCheckResult
{
    public bool DriftDetected { get; init; }
    public decimal RollingAuc { get; init; }
    public decimal RollingBrier { get; init; }
    public decimal ActualWinRate { get; init; }
    public decimal PredictedMeanWin { get; init; }
    public int WindowSize { get; init; }
    public bool AucDrift { get; init; }
    public bool BrierDrift { get; init; }
    public bool WinRateDrift { get; init; }
}
