using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Engine;

/// <summary>
/// Phase 9: Historical backtest engine.
/// Replays closed candles through the indicator + regime + signal + risk pipeline.
/// Uses only data available up to each candle close (no look-ahead).
/// </summary>
public static class BacktestEngine
{
    public record BacktestResult(
        IReadOnlyList<SignalRecommendation> Signals,
        IReadOnlyList<SignalOutcome> Outcomes,
        PerformanceStats Stats);

    /// <summary>
    /// Run backtest over closed 5m candles with their corresponding 15m regime snapshots.
    /// </summary>
    public static BacktestResult Run(
        string symbol,
        IReadOnlyList<RichCandle> candles5m,
        IReadOnlyList<RichCandle> candles15m,
        RiskPolicy policy,
        IReadOnlyList<RichCandle>? candles1m = null)
    {
        if (candles5m.Count < IndicatorEngine.WarmUpPeriod + 10 ||
            candles15m.Count < IndicatorEngine.WarmUpPeriod + 10)
            return new BacktestResult([], [], new PerformanceStats());

        // Precompute all 5m and 15m indicators
        var snaps5m = IndicatorEngine.ComputeAll(symbol, "5m", candles5m);
        var snaps15m = IndicatorEngine.ComputeAll(symbol, "15m", candles15m);

        // Build regime timeline from 15m snapshots
        var regimeByTime = new Dictionary<DateTimeOffset, RegimeResult>();
        var final15m = snaps15m.Where(s => !s.IsProvisional).ToList();
        for (int i = 4; i < final15m.Count; i++)
        {
            var window = final15m.Skip(i - 3).Take(4).ToList();
            var regime = RegimeAnalyzer.Classify(symbol, window);
            regimeByTime[final15m[i].CandleOpenTimeUtc] = regime;
        }

        var signals = new List<SignalRecommendation>();
        var outcomes = new List<SignalOutcome>();

        // Walk through 5m candles, using only data up to index i (no look-ahead)
        for (int i = IndicatorEngine.WarmUpPeriod; i < candles5m.Count - OutcomeEvaluator.TimeoutBars; i++)
        {
            var snap = snaps5m[i];
            if (snap.IsProvisional) continue;

            var prevSnap = i > 0 ? snaps5m[i - 1] : null;
            var candle = candles5m[i];

            // Find the most recent 15m regime at or before this 5m candle time
            var m15Floor = Timeframe.M15.Floor(candle.OpenTime);
            RegimeResult? regime = null;
            for (var t = m15Floor; t >= m15Floor.AddHours(-1); t = t.AddMinutes(-15))
            {
                if (regimeByTime.TryGetValue(t, out regime)) break;
            }
            if (regime == null) continue;

            var signal = SignalEngine.Evaluate(symbol, regime, snap, prevSnap, candle);
            if (signal.Direction == SignalDirection.NO_TRADE) continue;

            // Apply risk management
            decimal swingExtreme;
            if (signal.Direction == SignalDirection.BUY)
            {
                // Swing low = min of last 5 candles' lows
                swingExtreme = candles5m.Skip(Math.Max(0, i - 5)).Take(5).Min(c => c.MidLow);
            }
            else
            {
                swingExtreme = candles5m.Skip(Math.Max(0, i - 5)).Take(5).Max(c => c.MidHigh);
            }

            decimal spreadPct = snap.CloseMid > 0 ? snap.Spread / snap.CloseMid : 0;
            var risk = RiskManager.ComputeRisk(signal.Direction, snap.CloseMid, snap.Atr14, swingExtreme, spreadPct, policy);
            if (!risk.Allowed) continue;

            var fullSignal = signal with
            {
                EntryPrice = risk.EntryPrice,
                TpPrice = risk.TakeProfit,
                SlPrice = risk.StopLoss,
                RiskUsd = risk.RiskUsd,
                RiskPercent = risk.RiskPercent
            };

            if (i + 1 < candles5m.Count)
                fullSignal = RiskManager.ReanchorToFilledEntry(fullSignal, candles5m[i + 1].MidOpen);

            signals.Add(fullSignal);

            // Evaluate outcome using future candles (no look-ahead: we use candles after i)
            var futureCandles = candles5m.Skip(i + 1).Take(OutcomeEvaluator.TimeoutBars).ToList();
            IReadOnlyList<RichCandle>? future1m = null;
            if (candles1m != null && futureCandles.Count > 0)
            {
                var intrabarEnd = futureCandles[^1].OpenTime.Add(Timeframe.M5.Duration);
                future1m = candles1m
                    .Where(c => c.OpenTime >= futureCandles[0].OpenTime && c.OpenTime < intrabarEnd)
                    .ToList();
            }

            var outcome = OutcomeEvaluator.Evaluate(fullSignal, futureCandles, StrategyParameters.Default,
                Timeframe.M5, future1m, candles1m != null ? Timeframe.M1 : null);
            outcomes.Add(outcome);
        }

        var stats = OutcomeEvaluator.ComputeStats(outcomes);
        return new BacktestResult(signals, outcomes, stats);
    }
}
