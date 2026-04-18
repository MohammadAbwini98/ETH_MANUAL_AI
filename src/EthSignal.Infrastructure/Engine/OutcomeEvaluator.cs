using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Engine;

/// <summary>
/// Phase 6: Evaluates signal outcomes by examining future candles.
/// </summary>
public static class OutcomeEvaluator
{
    /// <summary>Legacy constant — use StrategyParameters.OutcomeTimeoutBars instead.</summary>
    public const int TimeoutBars = 60; // 60 × 5m = 5 hours max

    /// <summary>Evaluate using default parameters.</summary>
    public static SignalOutcome Evaluate(
        SignalRecommendation signal,
        IReadOnlyList<RichCandle> futureCandles)
        => Evaluate(signal, futureCandles, StrategyParameters.Default, Timeframe.M5);

    /// <summary>Evaluate a signal against subsequent candles using the given parameter set.</summary>
    public static SignalOutcome Evaluate(
        SignalRecommendation signal,
        IReadOnlyList<RichCandle> futureCandles,
        StrategyParameters p,
        Timeframe? timeframe = null,
        IReadOnlyList<RichCandle>? intrabarCandles = null,
        Timeframe? intrabarTimeframe = null)
    {
        var tf = timeframe ?? Timeframe.M5;
        int timeoutBars = GetTimeoutBars(p, tf);
        if (signal.Direction == SignalDirection.NO_TRADE)
            return MakeOutcome(signal.SignalId, 0, OutcomeLabel.EXPIRED, 0, 0, 0, 0, 0, null);

        bool isBuy = signal.Direction == SignalDirection.BUY;
        decimal entry = signal.EntryPrice;
        decimal tp = signal.TpPrice;
        decimal sl = signal.SlPrice;
        decimal stopDist = Math.Abs(entry - sl);

        // Multi-target mode: activated when Tp1Price is set and in-direction of trade.
        // TP1 closes 40% of position and moves SL to breakeven.
        // TP2 closes 30% more. Final TP (Tp3 or TpPrice) closes remaining 30%.
        // This prevents breakeven exits from being counted as losses.
        bool useMultiTarget = stopDist > 0
            && signal.Tp1Price > 0
            && (isBuy ? signal.Tp1Price > entry : signal.Tp1Price < entry);
        decimal tp1 = signal.Tp1Price;
        decimal tp2 = signal.Tp2Price > 0
            ? signal.Tp2Price
            : (isBuy ? entry + stopDist * 2m : entry - stopDist * 2m);
        decimal tp3 = signal.Tp3Price > 0 ? signal.Tp3Price : tp;

        // R-values per TP level (for blended PnL)
        decimal tp1R = stopDist > 0 ? (isBuy ? (tp1 - entry) : (entry - tp1)) / stopDist : 0;
        decimal tp2R = stopDist > 0 ? (isBuy ? (tp2 - entry) : (entry - tp2)) / stopDist : 0;
        decimal tp3R = stopDist > 0 ? (isBuy ? (tp3 - entry) : (entry - tp3)) / stopDist : 0;
        // Position size allocations: 40% at TP1, 30% at TP2, 30% at TP3
        const decimal a1 = 0.4m, a2 = 0.3m, a3 = 0.3m;

        bool tp1Hit = false;
        bool tp2Hit = false;
        decimal activeSl = sl; // promoted to entry (breakeven) after TP1 hit

        decimal mfePrice = entry;
        decimal maePrice = entry;

        for (int i = 0; i < futureCandles.Count && i < timeoutBars; i++)
        {
            var c = futureCandles[i];
            decimal high = c.MidHigh;
            decimal low = c.MidLow;
            var candleCloseTime = c.OpenTime.Add(tf.Duration);

            if (isBuy)
            {
                if (high > mfePrice) mfePrice = high;
                if (low < maePrice) maePrice = low;

                if (useMultiTarget)
                {
                    // Promote TP1 → activeSl becomes breakeven
                    if (!tp1Hit && high >= tp1)
                    {
                        tp1Hit = true;
                        activeSl = entry;
                        // Same-candle TP2 promotion
                        if (high >= tp2)
                        {
                            tp2Hit = true;
                            // Same-candle final TP
                            if (high >= tp3)
                            {
                                decimal blended = a1 * tp1R + a2 * tp2R + a3 * tp3R;
                                return MakeOutcome(signal.SignalId, i + 1, OutcomeLabel.WIN,
                                    blended, mfePrice, maePrice,
                                    (mfePrice - entry) / stopDist,
                                    (entry - maePrice) / stopDist, candleCloseTime);
                            }
                        }
                    }
                    else if (tp1Hit && !tp2Hit && high >= tp2)
                    {
                        tp2Hit = true;
                        if (high >= tp3)
                        {
                            decimal blended = a1 * tp1R + a2 * tp2R + a3 * tp3R;
                            return MakeOutcome(signal.SignalId, i + 1, OutcomeLabel.WIN,
                                blended, mfePrice, maePrice,
                                (mfePrice - entry) / stopDist,
                                (entry - maePrice) / stopDist, candleCloseTime);
                        }
                    }
                    else if (tp1Hit && tp2Hit && high >= tp3)
                    {
                        decimal blended = a1 * tp1R + a2 * tp2R + a3 * tp3R;
                        return MakeOutcome(signal.SignalId, i + 1, OutcomeLabel.WIN,
                            blended, mfePrice, maePrice,
                            (mfePrice - entry) / stopDist,
                            (entry - maePrice) / stopDist, candleCloseTime);
                    }

                    if (low <= activeSl)
                    {
                        if (tp1Hit)
                        {
                            // SL hit after TP1 (at breakeven or better) — partial WIN
                            decimal partialPnl = a1 * tp1R + (tp2Hit ? a2 * tp2R : 0);
                            return MakeOutcome(signal.SignalId, i + 1, OutcomeLabel.WIN,
                                partialPnl, mfePrice, maePrice,
                                (mfePrice - entry) / stopDist,
                                (entry - maePrice) / stopDist, candleCloseTime);
                        }
                        // SL hit before TP1 — full loss
                        decimal pnl = stopDist > 0 ? (sl - entry) / stopDist : 0;
                        return MakeOutcome(signal.SignalId, i + 1, OutcomeLabel.LOSS,
                            pnl, mfePrice, maePrice,
                            (mfePrice - entry) / stopDist,
                            (entry - maePrice) / stopDist, candleCloseTime);
                    }
                }
                else
                {
                    bool tpHit = high >= tp;
                    bool slHit = low <= sl;

                    if (tpHit && slHit)
                    {
                        var resolved = ResolveAmbiguousOutcome(signal, c, i + 1, tf, stopDist,
                            mfePrice, maePrice, intrabarCandles, intrabarTimeframe);
                        if (resolved is not null) return resolved;
                        return MakeOutcome(signal.SignalId, i + 1, OutcomeLabel.AMBIGUOUS,
                            0, mfePrice, maePrice,
                            stopDist > 0 ? (mfePrice - entry) / stopDist : 0,
                            stopDist > 0 ? (entry - maePrice) / stopDist : 0,
                            candleCloseTime);
                    }
                    if (tpHit)
                    {
                        decimal pnl = stopDist > 0 ? (tp - entry) / stopDist : 0;
                        return MakeOutcome(signal.SignalId, i + 1, OutcomeLabel.WIN,
                            pnl, mfePrice, maePrice,
                            stopDist > 0 ? (mfePrice - entry) / stopDist : 0,
                            stopDist > 0 ? (entry - maePrice) / stopDist : 0,
                            candleCloseTime);
                    }
                    if (slHit)
                    {
                        decimal pnl = stopDist > 0 ? (sl - entry) / stopDist : 0;
                        return MakeOutcome(signal.SignalId, i + 1, OutcomeLabel.LOSS,
                            pnl, mfePrice, maePrice,
                            stopDist > 0 ? (mfePrice - entry) / stopDist : 0,
                            stopDist > 0 ? (entry - maePrice) / stopDist : 0,
                            candleCloseTime);
                    }
                }
            }
            else // SELL
            {
                if (low < mfePrice) mfePrice = low;
                if (high > maePrice) maePrice = high;

                if (useMultiTarget)
                {
                    // Promote TP1 → activeSl becomes breakeven
                    if (!tp1Hit && low <= tp1)
                    {
                        tp1Hit = true;
                        activeSl = entry;
                        if (low <= tp2)
                        {
                            tp2Hit = true;
                            if (low <= tp3)
                            {
                                decimal blended = a1 * tp1R + a2 * tp2R + a3 * tp3R;
                                return MakeOutcome(signal.SignalId, i + 1, OutcomeLabel.WIN,
                                    blended, mfePrice, maePrice,
                                    (entry - mfePrice) / stopDist,
                                    (maePrice - entry) / stopDist, candleCloseTime);
                            }
                        }
                    }
                    else if (tp1Hit && !tp2Hit && low <= tp2)
                    {
                        tp2Hit = true;
                        if (low <= tp3)
                        {
                            decimal blended = a1 * tp1R + a2 * tp2R + a3 * tp3R;
                            return MakeOutcome(signal.SignalId, i + 1, OutcomeLabel.WIN,
                                blended, mfePrice, maePrice,
                                (entry - mfePrice) / stopDist,
                                (maePrice - entry) / stopDist, candleCloseTime);
                        }
                    }
                    else if (tp1Hit && tp2Hit && low <= tp3)
                    {
                        decimal blended = a1 * tp1R + a2 * tp2R + a3 * tp3R;
                        return MakeOutcome(signal.SignalId, i + 1, OutcomeLabel.WIN,
                            blended, mfePrice, maePrice,
                            (entry - mfePrice) / stopDist,
                            (maePrice - entry) / stopDist, candleCloseTime);
                    }

                    if (high >= activeSl)
                    {
                        if (tp1Hit)
                        {
                            decimal partialPnl = a1 * tp1R + (tp2Hit ? a2 * tp2R : 0);
                            return MakeOutcome(signal.SignalId, i + 1, OutcomeLabel.WIN,
                                partialPnl, mfePrice, maePrice,
                                (entry - mfePrice) / stopDist,
                                (maePrice - entry) / stopDist, candleCloseTime);
                        }
                        decimal pnl = stopDist > 0 ? (entry - sl) / stopDist : 0;
                        return MakeOutcome(signal.SignalId, i + 1, OutcomeLabel.LOSS,
                            pnl, mfePrice, maePrice,
                            (entry - mfePrice) / stopDist,
                            (maePrice - entry) / stopDist, candleCloseTime);
                    }
                }
                else
                {
                    bool tpHit = low <= tp;
                    bool slHit = high >= sl;

                    if (tpHit && slHit)
                    {
                        var resolved = ResolveAmbiguousOutcome(signal, c, i + 1, tf, stopDist,
                            mfePrice, maePrice, intrabarCandles, intrabarTimeframe);
                        if (resolved is not null) return resolved;
                        return MakeOutcome(signal.SignalId, i + 1, OutcomeLabel.AMBIGUOUS,
                            0, mfePrice, maePrice,
                            stopDist > 0 ? (entry - mfePrice) / stopDist : 0,
                            stopDist > 0 ? (maePrice - entry) / stopDist : 0,
                            candleCloseTime);
                    }
                    if (tpHit)
                    {
                        decimal pnl = stopDist > 0 ? (entry - tp) / stopDist : 0;
                        return MakeOutcome(signal.SignalId, i + 1, OutcomeLabel.WIN,
                            pnl, mfePrice, maePrice,
                            stopDist > 0 ? (entry - mfePrice) / stopDist : 0,
                            stopDist > 0 ? (maePrice - entry) / stopDist : 0,
                            candleCloseTime);
                    }
                    if (slHit)
                    {
                        decimal pnl = stopDist > 0 ? (entry - sl) / stopDist : 0;
                        return MakeOutcome(signal.SignalId, i + 1, OutcomeLabel.LOSS,
                            pnl, mfePrice, maePrice,
                            stopDist > 0 ? (entry - mfePrice) / stopDist : 0,
                            stopDist > 0 ? (maePrice - entry) / stopDist : 0,
                            candleCloseTime);
                    }
                }
            }
        }

        // B-02 FIX: If we haven't observed enough bars yet, signal is still PENDING
        if (futureCandles.Count < timeoutBars)
        {
            return MakeOutcome(signal.SignalId, futureCandles.Count, OutcomeLabel.PENDING,
                0, mfePrice, maePrice, 0, 0, null);
        }

        // Timeout — if TP1 was hit in multi-target mode, runner closes at last candle price
        if (useMultiTarget && tp1Hit)
        {
            var lastClose = futureCandles[^1].MidClose;
            decimal runnerPnlR = stopDist > 0
                ? (isBuy ? (lastClose - entry) : (entry - lastClose)) / stopDist
                : 0;
            decimal openAlloc = tp2Hit ? a3 : (a2 + a3);
            decimal totalPnl = a1 * tp1R + (tp2Hit ? a2 * tp2R : 0) + openAlloc * runnerPnlR;
            var expiredTime2 = futureCandles[^1].OpenTime.Add(tf.Duration);
            return MakeOutcome(signal.SignalId, timeoutBars,
                totalPnl >= 0 ? OutcomeLabel.WIN : OutcomeLabel.EXPIRED,
                totalPnl, mfePrice, maePrice,
                stopDist > 0 ? (isBuy ? (mfePrice - entry) : (entry - mfePrice)) / stopDist : 0,
                stopDist > 0 ? (isBuy ? (entry - maePrice) : (maePrice - entry)) / stopDist : 0,
                expiredTime2);
        }

        // Timeout — expired (we observed timeoutBars candles with no TP/SL hit)
        decimal finalPnl = 0;
        if (stopDist > 0)
        {
            var lastClose = futureCandles[^1].MidClose;
            finalPnl = isBuy ? (lastClose - entry) / stopDist : (entry - lastClose) / stopDist;
        }

        var expiredTime = futureCandles[^1].OpenTime.Add(tf.Duration);
        return MakeOutcome(signal.SignalId, timeoutBars,
            OutcomeLabel.EXPIRED, finalPnl, mfePrice, maePrice,
            stopDist > 0 ? (isBuy ? (mfePrice - entry) : (entry - mfePrice)) / stopDist : 0,
            stopDist > 0 ? (isBuy ? (entry - maePrice) : (maePrice - entry)) / stopDist : 0,
            expiredTime);
    }

    public static PerformanceStats ComputeStats(IReadOnlyList<SignalOutcome> outcomes)
    {
        var resolved = outcomes.Where(o => o.OutcomeLabel != OutcomeLabel.PENDING).ToList();
        var metricEligible = resolved.Where(o => o.OutcomeLabel != OutcomeLabel.AMBIGUOUS).ToList();
        int wins = metricEligible.Count(o => o.OutcomeLabel == OutcomeLabel.WIN);
        int losses = metricEligible.Count(o => o.OutcomeLabel == OutcomeLabel.LOSS);
        int expired = metricEligible.Count(o => o.OutcomeLabel == OutcomeLabel.EXPIRED);
        int ambiguous = resolved.Count(o => o.OutcomeLabel == OutcomeLabel.AMBIGUOUS);

        decimal totalPnl = metricEligible.Sum(o => o.PnlR);
        decimal sumPositive = metricEligible.Where(o => o.PnlR > 0).Sum(o => o.PnlR);
        decimal sumNegative = Math.Abs(metricEligible.Where(o => o.PnlR < 0).Sum(o => o.PnlR));
        decimal profitFactor = sumNegative > 0 ? sumPositive / sumNegative : sumPositive > 0 ? 999m : 0m;

        // T3-13: Win rate denominator is (wins + losses) only — EXPIRED signals are excluded.
        // EXPIRED means the trade timed out without hitting TP or SL; including them in the
        // denominator penalizes entry quality even when the timeout window was too tight.
        // Profit factor and AverageR still use all resolved (including EXPIRED) for conservatism.
        int decidedCount = wins + losses;

        return new PerformanceStats
        {
            TotalSignals = outcomes.Count,
            ResolvedSignals = resolved.Count,
            Wins = wins,
            Losses = losses,
            Expired = expired,
            Ambiguous = ambiguous,
            WinRate = decidedCount > 0 ? (decimal)wins / decidedCount * 100m : 0,
            AverageR = metricEligible.Count > 0 ? totalPnl / metricEligible.Count : 0,
            ProfitFactor = profitFactor,
            TotalPnlR = totalPnl
        };
    }

    private static SignalOutcome? ResolveAmbiguousOutcome(
        SignalRecommendation signal,
        RichCandle parentCandle,
        int barsObserved,
        Timeframe timeframe,
        decimal stopDist,
        decimal mfePrice,
        decimal maePrice,
        IReadOnlyList<RichCandle>? intrabarCandles,
        Timeframe? intrabarTimeframe)
    {
        if (intrabarCandles == null || intrabarCandles.Count == 0)
            return null;

        var intrabarTf = intrabarTimeframe ?? Timeframe.M1;
        if (intrabarTf.Duration >= timeframe.Duration)
            return null;

        var subCandles = intrabarCandles
            .Where(c => c.OpenTime >= parentCandle.OpenTime && c.OpenTime < parentCandle.OpenTime.Add(timeframe.Duration))
            .OrderBy(c => c.OpenTime)
            .ToList();

        if (subCandles.Count == 0)
            return null;

        bool isBuy = signal.Direction == SignalDirection.BUY;
        decimal entry = signal.EntryPrice;
        decimal tp = signal.TpPrice;
        decimal sl = signal.SlPrice;

        foreach (var sub in subCandles)
        {
            bool tpHit = isBuy ? sub.MidHigh >= tp : sub.MidLow <= tp;
            bool slHit = isBuy ? sub.MidLow <= sl : sub.MidHigh >= sl;
            if (!tpHit && !slHit)
                continue;

            if (tpHit && slHit)
                return null;

            var closedAt = sub.OpenTime.Add(intrabarTf.Duration);
            if (tpHit)
            {
                decimal pnl = stopDist > 0 ? (isBuy ? (tp - entry) : (entry - tp)) / stopDist : 0;
                return MakeOutcome(signal.SignalId, barsObserved, OutcomeLabel.WIN,
                    pnl, mfePrice, maePrice,
                    stopDist > 0 ? (isBuy ? (mfePrice - entry) : (entry - mfePrice)) / stopDist : 0,
                    stopDist > 0 ? (isBuy ? (entry - maePrice) : (maePrice - entry)) / stopDist : 0,
                    closedAt);
            }

            decimal lossPnl = stopDist > 0 ? (isBuy ? (sl - entry) : (entry - sl)) / stopDist : 0;
            return MakeOutcome(signal.SignalId, barsObserved, OutcomeLabel.LOSS,
                lossPnl, mfePrice, maePrice,
                stopDist > 0 ? (isBuy ? (mfePrice - entry) : (entry - mfePrice)) / stopDist : 0,
                stopDist > 0 ? (isBuy ? (entry - maePrice) : (maePrice - entry)) / stopDist : 0,
                closedAt);
        }

        return null;
    }

    private static SignalOutcome MakeOutcome(Guid signalId, int bars, OutcomeLabel label,
        decimal pnlR, decimal mfePrice, decimal maePrice, decimal mfeR, decimal maeR,
        DateTimeOffset? closedAt) => new()
        {
            SignalId = signalId,
            BarsObserved = bars,
            TpHit = label == OutcomeLabel.WIN,
            SlHit = label == OutcomeLabel.LOSS,
            OutcomeLabel = label,
            PnlR = pnlR,
            MfePrice = mfePrice,
            MaePrice = maePrice,
            MfeR = mfeR,
            MaeR = maeR,
            // P6-03 FIX: use candle close time instead of evaluation time
            ClosedAtUtc = closedAt
        };

    /// <summary>
    /// FR-3: Select the appropriate timeout based on the signal's timeframe category.
    /// Scalp (1m) → ScalpTimeoutBars, Intraday (5m/15m/30m) → IntradayTimeoutBars,
    /// Higher (1h/4h) → HigherTfTimeoutBars. Falls back to OutcomeTimeoutBars when 0.
    /// </summary>
    public static int GetTimeoutBars(StrategyParameters p, Timeframe tf)
    {
        int specificTimeout = tf.Minutes switch
        {
            1 => p.ScalpTimeoutBars,
            >= 5 and <= 30 => p.IntradayTimeoutBars,
            >= 60 => p.HigherTfTimeoutBars,
            _ => 0
        };
        return specificTimeout > 0 ? specificTimeout : p.OutcomeTimeoutBars;
    }
}
