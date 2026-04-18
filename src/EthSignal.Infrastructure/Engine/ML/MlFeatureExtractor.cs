using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Engine.ML;

/// <summary>
/// Builds the ML feature vector from current indicators, candle, regime, and lookback data.
/// Uses only data available at evaluation time (no look-ahead).
/// </summary>
public static class MlFeatureExtractor
{
    public const string FeatureVersion = "v3.0";

    /// <summary>
    /// Build feature vector for current evaluation.
    /// </summary>
    /// <param name="snap">Current indicator snapshot.</param>
    /// <param name="prevSnap">Previous bar indicator snapshot (may be null).</param>
    /// <param name="recentSnaps">Last 20 indicator snapshots for lookback features (newest first).</param>
    /// <param name="candle">Current closed candle.</param>
    /// <param name="regime">Current regime classification.</param>
    /// <param name="direction">Candidate signal direction.</param>
    /// <param name="ruleBasedScore">Score from rule-based engine.</param>
    /// <param name="recentOutcomes">Last 20 resolved outcomes (newest first).</param>
    /// <param name="barsSinceLastSignal">Bars since last signal was generated.</param>
    /// <param name="evaluationTf">Timeframe for regime age calculation.</param>
    /// <param name="recentSignals">Recent signals for saturation features (newest first, optional).</param>
    /// <param name="btcContext">Optional BTC cross-asset context.</param>
    public static MlFeatureVector Extract(
        IndicatorSnapshot snap,
        IndicatorSnapshot? prevSnap,
        IReadOnlyList<IndicatorSnapshot> recentSnaps,
        RichCandle candle,
        RegimeResult regime,
        SignalDirection direction,
        int ruleBasedScore,
        IReadOnlyList<SignalOutcome> recentOutcomes,
        int barsSinceLastSignal,
        Timeframe? evaluationTf = null,
        IReadOnlyList<SignalRecommendation>? recentSignals = null,
        BtcCrossAssetContext? btcContext = null,
        Guid? evaluationId = null)
    {
        var evalId = evaluationId ?? Guid.NewGuid();
        var ts = snap.CandleOpenTimeUtc;

        // ─── Category A: Raw indicators (14) ────────────
        decimal bodyRatio = SignalEngine.ComputeBodyRatio(candle);

        // ─── Category B: Derived features (18) ──────────
        decimal ema20MinusEma50 = snap.Ema20 - snap.Ema50;
        decimal ema20MinusEma50Pct = SafeDiv(ema20MinusEma50, snap.CloseMid);
        decimal ema20Slope3 = ComputeSlope(recentSnaps, s => s.Ema20, 3);
        decimal ema20Slope5 = ComputeSlope(recentSnaps, s => s.Ema20, 5);
        decimal rsi14Delta = prevSnap != null ? snap.Rsi14 - prevSnap.Rsi14 : 0;
        decimal rsi14Delta3 = ComputeDelta(recentSnaps, s => s.Rsi14, 3);
        decimal macdHistDelta = prevSnap != null ? snap.MacdHist - prevSnap.MacdHist : 0;
        decimal macdHistDelta3 = ComputeDelta(recentSnaps, s => s.MacdHist, 3);
        decimal adx14Delta = prevSnap != null ? snap.Adx14 - prevSnap.Adx14 : 0;
        decimal atr14Pct = SafeDiv(snap.Atr14, snap.CloseMid);
        decimal atr14DeltaPct = prevSnap != null && prevSnap.Atr14 != 0
            ? (snap.Atr14 - prevSnap.Atr14) / prevSnap.Atr14 : 0;
        decimal distanceToEma20Pct = SafeDiv(snap.CloseMid - snap.Ema20, snap.CloseMid);
        decimal distanceToVwapPct = SafeDiv(snap.CloseMid - snap.Vwap, snap.CloseMid);
        decimal volumeRatio = snap.VolumeSma20 != 0 ? candle.Volume / snap.VolumeSma20 : 0;
        decimal spreadPct = SafeDiv(snap.Spread, snap.CloseMid);
        decimal diDifferential = snap.PlusDi - snap.MinusDi;
        decimal diRatio = SafeDiv(snap.PlusDi, Math.Max(snap.MinusDi, 0.01m));
        decimal candleRangePct = SafeDiv(candle.MidHigh - candle.MidLow, snap.CloseMid);

        // ─── Category C: Contextual features (12) ───────
        int regimeLabel = regime.Regime switch
        {
            Regime.NEUTRAL => 0,
            Regime.BULLISH => 1,
            Regime.BEARISH => 2,
            _ => 0
        };
        int regimeAgeBars = ComputeRegimeAgeBars(recentSnaps, regime, evaluationTf);
        int timeframeEncoded = MlFeatureVector.EncodeTimeframe(evaluationTf?.Name ?? snap.Timeframe);
        int hourOfDay = ts.UtcDateTime.Hour;
        int dayOfWeek = (int)ts.UtcDateTime.DayOfWeek;
        int minutesSinceOpen = hourOfDay * 60 + ts.UtcDateTime.Minute;
        bool isLondonSession = hourOfDay >= 7 && hourOfDay < 16;
        bool isNySession = hourOfDay >= 13 && hourOfDay < 21;
        bool isAsiaSession = hourOfDay >= 23 || hourOfDay < 7;
        bool isOverlap = hourOfDay >= 13 && hourOfDay < 16;

        // ─── Category D: Lookback features (14) ─────────
        // Outcome-based features from recent resolved signals
        var (winRate10, winRate20, avgPnlR10, avgPnlR20, consWins, consLosses) =
            ComputeOutcomeFeatures(recentOutcomes);
        decimal avgAtr20Bars = ComputeAvg(recentSnaps, s => s.Atr14, 20);
        decimal atrZscore = ComputeZscore(recentSnaps, s => s.Atr14, snap.Atr14, 20);
        decimal avgVolume10Bars = ComputeAvgVolumes(recentSnaps, 10);
        // Use current bar's VolumeSma20 (not raw volume) so the z-score is on a
        // consistent smoothed scale — avoids apples-to-oranges comparison.
        decimal volumeZscore = ComputeVolumeZscore(recentSnaps, snap.VolumeSma20, 10);
        decimal priceRange20BarsPct = ComputePriceRange(recentSnaps, snap.CloseMid, 20);
        int regimeChangesLast20 = ComputeRegimeChanges(recentSnaps);
        decimal pullbackDepthPct = ComputePullbackDepth(recentSnaps, 5);

        // ─── Category E: Market structure features (7) ──
        var (sessionRangePos, distPriorDayHigh, distPriorDayLow, distSessionVwap) =
            ComputeSessionStructure(recentSnaps, snap, candle, ts);
        var (rangePos20, distHigh20, distLow20) = ComputeRangePosition(recentSnaps, snap.CloseMid, 20);

        // ─── Category F: Volatility regime features (6) ─
        decimal realizedVol15m = ComputeRealizedVolatility(recentSnaps, 3);   // ~15m at 5m bars
        decimal realizedVol1h = ComputeRealizedVolatility(recentSnaps, 12);   // ~1h at 5m bars
        decimal realizedVol4h = ComputeRealizedVolatility(recentSnaps, 48);   // ~4h at 5m bars
        decimal volCompressionFlag = (realizedVol15m > 0 && realizedVol1h > 0 && realizedVol15m < 0.7m * realizedVol1h) ? 1m : 0m;
        decimal volExpansionFlag = (realizedVol15m > 0 && realizedVol1h > 0 && realizedVol15m > 1.5m * realizedVol1h) ? 1m : 0m;
        decimal atrPercentileRank = ComputePercentileRank(recentSnaps, s => s.Atr14, snap.Atr14, 50);

        // ─── Category G: Signal saturation features (5) ─
        var (sigLast10, sameDirLast10, oppDirLast10) = ComputeSignalSaturation(recentSignals, direction, 10);
        var (stopOutCount, falseBreakoutRate) = ComputeStopOutStats(recentOutcomes, 10);

        // ─── Category H: BTC cross-asset context (3) ────
        decimal btcRecentReturn = btcContext?.BtcRecentReturn ?? 0m;
        int btcRegimeLabel = btcContext?.BtcRegimeLabel ?? 0;
        decimal ethBtcRelativeStrength = btcContext?.EthBtcRelativeStrength ?? 0m;

        return new MlFeatureVector
        {
            EvaluationId = evalId,
            Timestamp = ts,
            Symbol = snap.Symbol,
            Timeframe = snap.Timeframe,

            // Category A
            Ema20 = snap.Ema20,
            Ema50 = snap.Ema50,
            Rsi14 = snap.Rsi14,
            MacdHist = snap.MacdHist,
            Adx14 = snap.Adx14,
            PlusDi = snap.PlusDi,
            MinusDi = snap.MinusDi,
            Atr14 = snap.Atr14,
            Vwap = snap.Vwap,
            VolumeSma20 = snap.VolumeSma20,
            Spread = snap.Spread,
            CloseMid = snap.CloseMid,
            Volume = candle.Volume,
            BodyRatio = bodyRatio,

            // Category B
            Ema20MinusEma50 = ema20MinusEma50,
            Ema20MinusEma50Pct = ema20MinusEma50Pct,
            Ema20Slope3 = ema20Slope3,
            Ema20Slope5 = ema20Slope5,
            Rsi14Delta = rsi14Delta,
            Rsi14Delta3 = rsi14Delta3,
            MacdHistDelta = macdHistDelta,
            MacdHistDelta3 = macdHistDelta3,
            Adx14Delta = adx14Delta,
            Atr14Pct = atr14Pct,
            Atr14DeltaPct = atr14DeltaPct,
            DistanceToEma20Pct = distanceToEma20Pct,
            DistanceToVwapPct = distanceToVwapPct,
            VolumeRatio = volumeRatio,
            SpreadPct = spreadPct,
            DiDifferential = diDifferential,
            DiRatio = diRatio,
            CandleRangePct = candleRangePct,

            // Category C
            RegimeLabel = regimeLabel,
            RegimeScore = regime.RegimeScore,
            RegimeAgeBars = regimeAgeBars,
            RuleBasedScore = ruleBasedScore,
            DirectionEncoded = direction switch
            {
                SignalDirection.BUY => 1,
                SignalDirection.SELL => -1,
                _ => 0
            },
            TimeframeEncoded = timeframeEncoded,
            HourOfDay = hourOfDay,
            DayOfWeek = dayOfWeek,
            MinutesSinceOpen = minutesSinceOpen,
            IsLondonSession = isLondonSession,
            IsNySession = isNySession,
            IsAsiaSession = isAsiaSession,
            IsOverlap = isOverlap,

            // Category D
            RecentWinRate10 = winRate10,
            RecentWinRate20 = winRate20,
            RecentAvgPnlR10 = avgPnlR10,
            RecentAvgPnlR20 = avgPnlR20,
            ConsecutiveWins = consWins,
            ConsecutiveLosses = consLosses,
            BarsSinceLastSignal = barsSinceLastSignal,
            AvgAtr20Bars = avgAtr20Bars,
            AtrZscore = atrZscore,
            AvgVolume10Bars = avgVolume10Bars,
            VolumeZscore = volumeZscore,
            PriceRange20BarsPct = priceRange20BarsPct,
            RegimeChangesLast20 = regimeChangesLast20,
            PullbackDepthPct = pullbackDepthPct,

            // Category E
            SessionRangePositionPct = sessionRangePos,
            DistanceToPriorDayHighPct = distPriorDayHigh,
            DistanceToPriorDayLowPct = distPriorDayLow,
            DistanceToSessionVwapPct = distSessionVwap,
            RangePositionPct = rangePos20,
            DistanceTo20BarHighPct = distHigh20,
            DistanceTo20BarLowPct = distLow20,

            // Category F
            RealizedVol15m = realizedVol15m,
            RealizedVol1h = realizedVol1h,
            RealizedVol4h = realizedVol4h,
            VolatilityCompressionFlag = volCompressionFlag,
            VolatilityExpansionFlag = volExpansionFlag,
            AtrPercentileRank = atrPercentileRank,

            // Category G
            SignalsLast10Bars = sigLast10,
            SameDirectionSignalsLast10 = sameDirLast10,
            OppositeDirectionSignalsLast10 = oppDirLast10,
            RecentStopOutCount = stopOutCount,
            RecentFalseBreakoutRate = falseBreakoutRate,

            // Category H
            BtcRecentReturn = btcRecentReturn,
            BtcRegimeLabel = btcRegimeLabel,
            EthBtcRelativeStrength = ethBtcRelativeStrength
        };
    }

    // ─── Helpers ────────────────────────────────────────

    private static decimal SafeDiv(decimal numerator, decimal denominator)
        => denominator != 0 ? numerator / denominator : 0;

    private static (decimal winRate10, decimal winRate20, decimal avgPnlR10, decimal avgPnlR20, int consWins, int consLosses)
        ComputeOutcomeFeatures(IReadOnlyList<SignalOutcome> outcomes)
    {
        if (outcomes.Count == 0) return (0, 0, 0, 0, 0, 0);

        static (decimal winRate, decimal avgPnlR) Calc(IReadOnlyList<SignalOutcome> list, int window)
        {
            int n = Math.Min(window, list.Count);
            if (n == 0) return (0, 0);
            int wins = 0;
            decimal sumPnlR = 0;
            for (int i = 0; i < n; i++)
            {
                if (list[i].OutcomeLabel == OutcomeLabel.WIN) wins++;
                sumPnlR += list[i].PnlR;
            }
            return ((decimal)wins / n, sumPnlR / n);
        }

        var (wr10, apr10) = Calc(outcomes, 10);
        var (wr20, apr20) = Calc(outcomes, 20);

        int cWins = 0, cLosses = 0;
        if (outcomes.Count > 0 && outcomes[0].OutcomeLabel == OutcomeLabel.WIN)
        {
            foreach (var o in outcomes) { if (o.OutcomeLabel == OutcomeLabel.WIN) cWins++; else break; }
        }
        else if (outcomes.Count > 0 && outcomes[0].OutcomeLabel == OutcomeLabel.LOSS)
        {
            foreach (var o in outcomes) { if (o.OutcomeLabel == OutcomeLabel.LOSS) cLosses++; else break; }
        }

        return (wr10, wr20, apr10, apr20, cWins, cLosses);
    }

    private static decimal ComputeSlope(IReadOnlyList<IndicatorSnapshot> snaps, Func<IndicatorSnapshot, decimal> selector, int periods)
    {
        if (snaps.Count < periods + 1) return 0;
        // snaps[0] is most recent, snaps[periods] is periods ago
        return (selector(snaps[0]) - selector(snaps[periods])) / periods;
    }

    private static decimal ComputeDelta(IReadOnlyList<IndicatorSnapshot> snaps, Func<IndicatorSnapshot, decimal> selector, int periods)
    {
        if (snaps.Count < periods + 1) return 0;
        return selector(snaps[0]) - selector(snaps[periods]);
    }

    private static decimal ComputeAvg(IReadOnlyList<IndicatorSnapshot> snaps, Func<IndicatorSnapshot, decimal> selector, int window)
    {
        if (snaps.Count == 0) return 0;
        int count = Math.Min(window, snaps.Count);
        decimal sum = 0;
        for (int i = 0; i < count; i++)
            sum += selector(snaps[i]);
        return sum / count;
    }

    private static decimal ComputeZscore(IReadOnlyList<IndicatorSnapshot> snaps, Func<IndicatorSnapshot, decimal> selector, decimal currentValue, int window)
    {
        if (snaps.Count < 2) return 0;
        int count = Math.Min(window, snaps.Count);
        decimal sum = 0, sumSq = 0;
        for (int i = 0; i < count; i++)
        {
            var v = selector(snaps[i]);
            sum += v;
            sumSq += v * v;
        }
        decimal mean = sum / count;
        decimal variance = sumSq / count - mean * mean;
        if (variance <= 0) return 0;
        decimal std = (decimal)Math.Sqrt((double)variance);
        return std > 0 ? (currentValue - mean) / std : 0;
    }

    private static decimal ComputeAvgVolumes(IReadOnlyList<IndicatorSnapshot> snaps, int window)
    {
        // Use VolumeSma20 as a proxy since we don't have raw volumes in snapshots
        return ComputeAvg(snaps, s => s.VolumeSma20, window);
    }

    private static decimal ComputeVolumeZscore(IReadOnlyList<IndicatorSnapshot> snaps, decimal currentVolume, int window)
    {
        return ComputeZscore(snaps, s => s.VolumeSma20, currentVolume, window);
    }

    private static decimal ComputePriceRange(IReadOnlyList<IndicatorSnapshot> snaps, decimal currentMid, int window)
    {
        if (snaps.Count == 0 || currentMid == 0) return 0;
        int count = Math.Min(window, snaps.Count);
        decimal max = decimal.MinValue, min = decimal.MaxValue;
        for (int i = 0; i < count; i++)
        {
            var c = snaps[i].CloseMid;
            if (c > max) max = c;
            if (c < min) min = c;
        }
        decimal mid = (max + min) / 2m;
        return mid > 0 ? (max - min) / mid : 0;
    }

    private static int ComputeRegimeChanges(IReadOnlyList<IndicatorSnapshot> snaps)
    {
        // Approximate regime changes by counting EMA20 vs EMA50 crossover flips
        // in the last 20 snapshots. A flip = sign(EMA20 - EMA50) changed from previous bar.
        if (snaps.Count < 2) return 0;
        int count = Math.Min(20, snaps.Count);
        int changes = 0;
        // snaps[0] is most recent; iterate from oldest to newest within window
        for (int i = count - 1; i >= 1; i--)
        {
            var prev = snaps[i];
            var curr = snaps[i - 1];
            if (prev.Ema20 == 0 || curr.Ema20 == 0) continue;
            bool prevBull = prev.Ema20 > prev.Ema50;
            bool currBull = curr.Ema20 > curr.Ema50;
            if (prevBull != currBull) changes++;
        }
        return changes;
    }

    private static decimal ComputePullbackDepth(IReadOnlyList<IndicatorSnapshot> snaps, int window)
    {
        if (snaps.Count == 0) return 0;
        int count = Math.Min(window, snaps.Count);
        decimal maxDistance = 0;
        for (int i = 0; i < count; i++)
        {
            var s = snaps[i];
            if (s.CloseMid == 0) continue;
            decimal dist = Math.Abs(s.CloseMid - s.Ema20) / s.CloseMid;
            if (dist > maxDistance) maxDistance = dist;
        }
        return maxDistance;
    }

    private static int ComputeRegimeAgeBars(IReadOnlyList<IndicatorSnapshot> snaps, RegimeResult regime, Timeframe? evaluationTf)
    {
        // Approximate: count bars since the regime candle time
        if (snaps.Count == 0) return 0;
        var latestTime = snaps[0].CandleOpenTimeUtc;
        var regimeTime = regime.CandleOpenTimeUtc;
        if (latestTime <= regimeTime) return 0;
        int tfMinutes = evaluationTf?.Minutes ?? 5;
        return (int)((latestTime - regimeTime).TotalMinutes / tfMinutes);
    }

    private static (decimal winRate10, decimal winRate20) ComputeWinRates(IReadOnlyList<SignalOutcome> outcomes)
    {
        decimal wr10 = ComputeWinRate(outcomes, 10);
        decimal wr20 = ComputeWinRate(outcomes, 20);
        return (wr10, wr20);
    }

    private static decimal ComputeWinRate(IReadOnlyList<SignalOutcome> outcomes, int window)
    {
        if (outcomes.Count == 0) return 0;
        int count = Math.Min(window, outcomes.Count);
        int wins = 0, resolved = 0;
        for (int i = 0; i < count; i++)
        {
            if (outcomes[i].OutcomeLabel is OutcomeLabel.WIN or OutcomeLabel.LOSS)
            {
                resolved++;
                if (outcomes[i].OutcomeLabel == OutcomeLabel.WIN) wins++;
            }
        }
        return resolved > 0 ? (decimal)wins / resolved : 0;
    }

    private static (decimal avg10, decimal avg20) ComputeAvgPnlR(IReadOnlyList<SignalOutcome> outcomes)
    {
        decimal avg10 = ComputeAvgPnlRWindow(outcomes, 10);
        decimal avg20 = ComputeAvgPnlRWindow(outcomes, 20);
        return (avg10, avg20);
    }

    private static decimal ComputeAvgPnlRWindow(IReadOnlyList<SignalOutcome> outcomes, int window)
    {
        if (outcomes.Count == 0) return 0;
        int count = Math.Min(window, outcomes.Count);
        decimal sum = 0;
        int resolved = 0;
        for (int i = 0; i < count; i++)
        {
            if (outcomes[i].OutcomeLabel is OutcomeLabel.WIN or OutcomeLabel.LOSS)
            {
                sum += outcomes[i].PnlR;
                resolved++;
            }
        }
        return resolved > 0 ? sum / resolved : 0;
    }

    private static (int consecutiveWins, int consecutiveLosses) ComputeStreaks(IReadOnlyList<SignalOutcome> outcomes)
    {
        int wins = 0, losses = 0;
        foreach (var o in outcomes)
        {
            // M-03: Skip unresolved outcomes before counting streaks
            if (o.OutcomeLabel is OutcomeLabel.PENDING or OutcomeLabel.EXPIRED)
                continue;

            if (o.OutcomeLabel == OutcomeLabel.WIN)
            {
                if (losses > 0) break;
                wins++;
            }
            else if (o.OutcomeLabel == OutcomeLabel.LOSS)
            {
                if (wins > 0) break;
                losses++;
            }
        }
        return (wins, losses);
    }

    // ─── Category E: Market structure helpers ────────────

    private static (decimal sessionRangePos, decimal distPriorDayHigh, decimal distPriorDayLow, decimal distSessionVwap)
        ComputeSessionStructure(IReadOnlyList<IndicatorSnapshot> snaps, IndicatorSnapshot snap, RichCandle candle, DateTimeOffset ts)
    {
        decimal close = snap.CloseMid;
        if (close == 0 || snaps.Count == 0)
            return (0, 0, 0, 0);

        // Determine session start: use the current UTC day's session boundary.
        // Session = bars from the start of the current UTC day.
        var dayStart = new DateTimeOffset(ts.UtcDateTime.Date, TimeSpan.Zero);

        decimal sessionHigh = candle.MidHigh;
        decimal sessionLow = candle.MidLow;
        decimal sessionVolWeightedSum = 0;
        decimal sessionVolSum = 0;

        decimal priorDayHigh = decimal.MinValue;
        decimal priorDayLow = decimal.MaxValue;
        bool hasPriorDay = false;

        foreach (var s in snaps)
        {
            if (s.CandleOpenTimeUtc >= dayStart)
            {
                // Current session
                if (s.MidHigh > sessionHigh) sessionHigh = s.MidHigh;
                if (s.MidLow > 0 && s.MidLow < sessionLow) sessionLow = s.MidLow;
                sessionVolWeightedSum += s.CloseMid * s.VolumeSma20;
                sessionVolSum += s.VolumeSma20;
            }
            else
            {
                // Prior day
                hasPriorDay = true;
                if (s.MidHigh > priorDayHigh) priorDayHigh = s.MidHigh;
                if (s.MidLow > 0 && s.MidLow < priorDayLow) priorDayLow = s.MidLow;
            }
        }

        decimal sessionRange = sessionHigh - sessionLow;
        decimal sessionRangePos = sessionRange > 0 ? (close - sessionLow) / sessionRange : 0.5m;
        sessionRangePos = Math.Clamp(sessionRangePos, 0m, 1m);

        decimal distPriorDayHigh = hasPriorDay ? SafeDiv(close - priorDayHigh, close) : 0;
        decimal distPriorDayLow = hasPriorDay ? SafeDiv(close - priorDayLow, close) : 0;

        // Approximate session VWAP from available bars
        decimal sessionVwap = sessionVolSum > 0 ? sessionVolWeightedSum / sessionVolSum : snap.Vwap;
        decimal distSessionVwap = SafeDiv(close - sessionVwap, close);

        return (sessionRangePos, distPriorDayHigh, distPriorDayLow, distSessionVwap);
    }

    private static (decimal rangePos, decimal distHigh, decimal distLow)
        ComputeRangePosition(IReadOnlyList<IndicatorSnapshot> snaps, decimal currentClose, int window)
    {
        if (snaps.Count == 0 || currentClose == 0) return (0.5m, 0, 0);

        int count = Math.Min(window, snaps.Count);
        decimal high = decimal.MinValue, low = decimal.MaxValue;
        for (int i = 0; i < count; i++)
        {
            if (snaps[i].MidHigh > high) high = snaps[i].MidHigh;
            if (snaps[i].MidLow > 0 && snaps[i].MidLow < low) low = snaps[i].MidLow;
        }

        decimal range = high - low;
        decimal rangePos = range > 0 ? Math.Clamp((currentClose - low) / range, 0m, 1m) : 0.5m;
        decimal distHigh = SafeDiv(currentClose - high, currentClose);
        decimal distLow = SafeDiv(currentClose - low, currentClose);
        return (rangePos, distHigh, distLow);
    }

    // ─── Category F: Volatility regime helpers ──────────

    private static decimal ComputeRealizedVolatility(IReadOnlyList<IndicatorSnapshot> snaps, int window)
    {
        // Realized volatility = std dev of log returns over the window
        if (snaps.Count < 2) return 0;
        int count = Math.Min(window, snaps.Count - 1);
        if (count < 1) return 0;

        decimal sumSq = 0;
        decimal sum = 0;
        int valid = 0;
        // snaps[0] is most recent; iterate from i to i+1 (older)
        for (int i = 0; i < count; i++)
        {
            var curr = snaps[i].CloseMid;
            var prev = snaps[i + 1].CloseMid;
            if (prev <= 0 || curr <= 0) continue;
            decimal logRet = (decimal)Math.Log((double)(curr / prev));
            sum += logRet;
            sumSq += logRet * logRet;
            valid++;
        }

        if (valid < 2) return 0;
        decimal mean = sum / valid;
        decimal variance = sumSq / valid - mean * mean;
        return variance > 0 ? (decimal)Math.Sqrt((double)variance) : 0;
    }

    private static decimal ComputePercentileRank(IReadOnlyList<IndicatorSnapshot> snaps,
        Func<IndicatorSnapshot, decimal> selector, decimal currentValue, int window)
    {
        if (snaps.Count == 0) return 0.5m;
        int count = Math.Min(window, snaps.Count);
        int below = 0;
        for (int i = 0; i < count; i++)
        {
            if (selector(snaps[i]) < currentValue) below++;
        }
        return (decimal)below / count;
    }

    // ─── Category G: Signal saturation helpers ──────────

    private static (int total, int sameDir, int oppositeDir) ComputeSignalSaturation(
        IReadOnlyList<SignalRecommendation>? recentSignals, SignalDirection currentDirection, int window)
    {
        if (recentSignals == null || recentSignals.Count == 0)
            return (0, 0, 0);

        int count = Math.Min(window, recentSignals.Count);
        int total = 0, same = 0, opposite = 0;
        for (int i = 0; i < count; i++)
        {
            var sig = recentSignals[i];
            if (sig.Direction == SignalDirection.NO_TRADE) continue;
            total++;
            if (sig.Direction == currentDirection)
                same++;
            else
                opposite++;
        }
        return (total, same, opposite);
    }

    private static (int stopOutCount, decimal falseBreakoutRate) ComputeStopOutStats(
        IReadOnlyList<SignalOutcome> outcomes, int window)
    {
        if (outcomes.Count == 0) return (0, 0);
        int count = Math.Min(window, outcomes.Count);
        int stopOuts = 0;
        int losses = 0;
        for (int i = 0; i < count; i++)
        {
            if (outcomes[i].OutcomeLabel == OutcomeLabel.LOSS)
            {
                losses++;
                if (outcomes[i].SlHit) stopOuts++;
            }
        }
        return (stopOuts, losses > 0 ? (decimal)stopOuts / losses : 0);
    }
}
