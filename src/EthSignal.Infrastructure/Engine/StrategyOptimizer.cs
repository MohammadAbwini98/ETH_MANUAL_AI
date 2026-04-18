using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Engine;

/// <summary>
/// Phase 7: Offline strategy optimization.
/// Analyzes historical signal performance and proposes new weights.
/// </summary>
public static class StrategyOptimizer
{
    public record FeatureRow(
        Guid SignalId,
        decimal RegimeScore,
        decimal Ema20MinusEma50,
        decimal Ema20Slope,
        decimal Rsi14,
        decimal MacdHist,
        decimal Adx14,
        decimal PlusDiMinusMinusDi,
        decimal DistanceToVwap,
        decimal VolumeRatio,
        decimal SpreadPct,
        decimal AtrPct,
        decimal BodyRatio,
        int HourOfDay,
        OutcomeLabel Outcome,
        decimal PnlR);

    /// <summary>
    /// Export feature dataset from stored signals and their indicator snapshots.
    /// </summary>
    public static IReadOnlyList<FeatureRow> ExportFeatures(
        IReadOnlyList<SignalRecommendation> signals,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        IReadOnlyList<SignalOutcome> outcomes,
        IReadOnlyList<RichCandle> candles)
    {
        var snapByTime = snapshots.ToDictionary(s => s.CandleOpenTimeUtc);
        var outcomeById = outcomes.ToDictionary(o => o.SignalId);
        var candleByTime = candles.ToDictionary(c => c.OpenTime);

        var rows = new List<FeatureRow>();

        foreach (var sig in signals)
        {
            if (!outcomeById.TryGetValue(sig.SignalId, out var outcome)) continue;
            if (!snapByTime.TryGetValue(sig.SignalTimeUtc, out var snap)) continue;

            candleByTime.TryGetValue(sig.SignalTimeUtc, out var candle);
            decimal bodyRatio = candle != null ? SignalEngine.ComputeBodyRatio(candle) : 0;

            rows.Add(new FeatureRow(
                SignalId: sig.SignalId,
                RegimeScore: sig.ConfidenceScore,
                Ema20MinusEma50: snap.Ema20 - snap.Ema50,
                Ema20Slope: 0, // Would need history for slope
                Rsi14: snap.Rsi14,
                MacdHist: snap.MacdHist,
                Adx14: snap.Adx14,
                PlusDiMinusMinusDi: snap.PlusDi - snap.MinusDi,
                DistanceToVwap: snap.CloseMid - snap.Vwap,
                VolumeRatio: snap.VolumeSma20 > 0 ? (candle?.Volume ?? 0) / snap.VolumeSma20 : 0,
                SpreadPct: snap.CloseMid > 0 ? snap.Spread / snap.CloseMid : 0,
                AtrPct: snap.CloseMid > 0 ? snap.Atr14 / snap.CloseMid : 0,
                BodyRatio: bodyRatio,
                HourOfDay: sig.SignalTimeUtc.Hour,
                Outcome: outcome.OutcomeLabel,
                PnlR: outcome.PnlR));
        }

        return rows;
    }

    /// <summary>
    /// Generate draft weights based on win rate per feature bin.
    /// Simple heuristic: increase weight of features that have higher win rate when active.
    /// </summary>
    public static StrategyVersion ProposeDraftWeights(
        IReadOnlyList<FeatureRow> features,
        StrategyVersion current)
    {
        if (features.Count < 10) return current with { Version = NextVersion(current.Version), IsDraft = true };

        // Compute win rate for signals where each condition was "good"
        decimal WinRate(Func<FeatureRow, bool> filter)
        {
            var subset = features.Where(filter).ToList();
            if (subset.Count == 0) return 0.5m;
            return (decimal)subset.Count(f => f.Outcome == OutcomeLabel.WIN) / subset.Count;
        }

        var wrEma = WinRate(f => f.Ema20MinusEma50 > 0);
        var wrRsi = WinRate(f => f.Rsi14 >= 40 && f.Rsi14 <= 60);
        var wrMacd = WinRate(f => f.MacdHist > 0);
        var wrAdx = WinRate(f => f.Adx14 >= 18);
        var wrVwap = WinRate(f => f.DistanceToVwap > 0);
        var wrVol = WinRate(f => f.VolumeRatio > 1.2m);
        var wrSpread = WinRate(f => f.SpreadPct < 0.003m);

        // Normalize to sum = 80 (regime always gets 20)
        var raw = new[] { wrEma, wrRsi, wrMacd, wrAdx, wrVwap, wrVol, wrSpread };
        var total = raw.Sum();
        if (total == 0) total = 1;

        int Scale(decimal wr) => Math.Max(5, (int)Math.Round(wr / total * 80));

        var weights = raw.Select(Scale).ToArray();
        var wSum = weights.Sum();
        // Adjust to make sum = 80
        weights[0] += 80 - wSum;

        return new StrategyVersion
        {
            Version = NextVersion(current.Version),
            IsDraft = true,
            WeightRegime = 20,
            WeightEma = weights[0],
            WeightRsi = weights[1],
            WeightMacd = weights[2],
            WeightAdx = weights[3],
            WeightVwap = weights[4],
            WeightVolume = weights[5],
            WeightSpread = weights[6],
            ActionableThreshold = current.ActionableThreshold
        };
    }

    private static string NextVersion(string current)
    {
        if (current.StartsWith("v") && decimal.TryParse(current[1..], out var ver))
            return $"v{ver + 0.1m:F1}";
        return current + "-draft";
    }
}
