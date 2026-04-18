using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Engine;

/// <summary>
/// Classifies 15m market regime into BULLISH, BEARISH, or NEUTRAL.
///
/// Mandatory conditions (must ALL pass for directional regime):
///   1. EMA20 vs EMA50 alignment
///   2. EMA20 slope direction
///   3. ADX >= threshold
///
/// Scored conditions (contribute to regime score but not mandatory):
///   4. Close vs VWAP
///   5. DI dominance (+DI vs -DI)
///   6. Market structure (HH/HL for bull, LH/LL for bear)
///
/// Regime score = mandatory conditions met (3) + scored conditions met (0-3).
/// Regime is directional if all 3 mandatory conditions pass.
/// </summary>
public static class RegimeAnalyzer
{
    /// <summary>Strategy version — bump when changing thresholds.</summary>
    public const string StrategyVersion = "v3.1";

    // Legacy constants — kept for backward compatibility
    private const int SlopeCandles = 3;
    private const decimal AdxThreshold = 15m;
    private const int StructureCandles = 4;

    /// <summary>Classify using default parameters.</summary>
    public static RegimeResult Classify(string symbol, IReadOnlyList<IndicatorSnapshot> snapshots)
        => Classify(symbol, snapshots, StrategyParameters.Default);

    /// <summary>Classify using the given parameter set.</summary>
    public static RegimeResult Classify(string symbol, IReadOnlyList<IndicatorSnapshot> snapshots, StrategyParameters p)
    {
        int slopeCandles = p.RegimeSlopeCandles;
        decimal adxThreshold = p.AdxTrendThreshold;
        int structureCandles = p.MarketStructureLookback;

        if (snapshots.Count == 0)
            return MakeNeutral(symbol, DateTimeOffset.UtcNow, 0, [], ["No snapshots available"]);

        var current = snapshots[^1];
        var candleTime = current.CandleOpenTimeUtc;

        if (snapshots.Count <= slopeCandles)
            return MakeNeutral(symbol, candleTime, 0, [], ["Insufficient history for slope calculation"]);

        var prev = snapshots[^(slopeCandles + 1)];
        decimal ema20Slope = (current.Ema20 - prev.Ema20) / slopeCandles;

        // ─── Evaluate BULLISH ─────────────────────────────
        var bullResult = EvaluateDirection(true, current, ema20Slope, snapshots, adxThreshold, structureCandles);

        if (bullResult.MandatoryPassed)
        {
            return new RegimeResult
            {
                Symbol = symbol,
                CandleOpenTimeUtc = candleTime,
                Regime = Regime.BULLISH,
                RegimeScore = bullResult.Score,
                TriggeredConditions = bullResult.Met,
                DisqualifyingConditions = bullResult.Failed
            };
        }

        // ─── Evaluate BEARISH ─────────────────────────────
        var bearResult = EvaluateDirection(false, current, ema20Slope, snapshots, adxThreshold, structureCandles);

        if (bearResult.MandatoryPassed)
        {
            return new RegimeResult
            {
                Symbol = symbol,
                CandleOpenTimeUtc = candleTime,
                Regime = Regime.BEARISH,
                RegimeScore = bearResult.Score,
                TriggeredConditions = bearResult.Met,
                DisqualifyingConditions = bearResult.Failed
            };
        }

        // ─── NEUTRAL ──────────────────────────────────────
        var allFailed = new List<string>();
        allFailed.AddRange(bullResult.Failed.Select(f => $"Bull: {f}"));
        allFailed.AddRange(bearResult.Failed.Select(f => $"Bear: {f}"));

        int bestScore = Math.Max(bullResult.Score, bearResult.Score);
        var bestMet = bestScore == bullResult.Score ? bullResult.Met : bearResult.Met;

        return MakeNeutral(symbol, candleTime, bestScore, bestMet, allFailed);
    }

    private record DirectionResult(bool MandatoryPassed, int Score, List<string> Met, List<string> Failed);

    private static DirectionResult EvaluateDirection(bool isBull, IndicatorSnapshot current,
        decimal ema20Slope, IReadOnlyList<IndicatorSnapshot> snapshots,
        decimal adxThreshold, int structureCandles)
    {
        var met = new List<string>();
        var failed = new List<string>();
        bool allMandatoryPassed = true;

        // ─── Mandatory conditions ─────────────────────────

        // 1. EMA alignment
        bool emaOk = isBull ? current.Ema20 > current.Ema50 : current.Ema20 < current.Ema50;
        if (emaOk)
            met.Add(isBull ? "EMA20 > EMA50" : "EMA20 < EMA50");
        else
        {
            failed.Add(isBull
                ? $"EMA20({current.Ema20:F2}) <= EMA50({current.Ema50:F2})"
                : $"EMA20({current.Ema20:F2}) >= EMA50({current.Ema50:F2})");
            allMandatoryPassed = false;
        }

        // 2. EMA20 slope
        bool slopeOk = isBull ? ema20Slope > 0 : ema20Slope < 0;
        if (slopeOk)
            met.Add($"Slope(EMA20)={ema20Slope:F6}");
        else
        {
            failed.Add($"Slope(EMA20)={ema20Slope:F6} wrong direction");
            allMandatoryPassed = false;
        }

        // 3. ADX strength
        bool adxOk = current.Adx14 >= adxThreshold;
        if (adxOk)
            met.Add($"ADX({current.Adx14:F1}) >= {adxThreshold}");
        else
        {
            failed.Add($"ADX({current.Adx14:F1}) < {adxThreshold}");
            allMandatoryPassed = false;
        }

        // ─── Scored (non-mandatory) conditions ────────────

        // 4. Close vs VWAP (skip in first few bars after UTC midnight when VWAP is unreliable)
        var minutesSinceMidnight = current.CandleOpenTimeUtc.UtcDateTime.TimeOfDay.TotalMinutes;
        if (minutesSinceMidnight < 25 || current.VolumeSma20 == 0)
        {
            failed.Add("VWAP skipped (insufficient daily volume)");
        }
        else
        {
            bool vwapOk = isBull ? current.CloseMid > current.Vwap : current.CloseMid < current.Vwap;
            if (vwapOk) met.Add("Close vs VWAP aligned");
            else failed.Add($"Close({current.CloseMid:F2}) vs VWAP({current.Vwap:F2}) not aligned");
        }

        // 5. DI dominance
        bool diOk = isBull ? current.PlusDi > current.MinusDi : current.MinusDi > current.PlusDi;
        if (diOk) met.Add(isBull ? "+DI > -DI" : "-DI > +DI");
        else failed.Add(isBull
            ? $"+DI({current.PlusDi:F1}) <= -DI({current.MinusDi:F1})"
            : $"-DI({current.MinusDi:F1}) <= +DI({current.PlusDi:F1})");

        // 6. Market structure (HH/HL for bull; LH/LL for bear)
        bool structureOk = EvaluateMarketStructure(isBull, snapshots, structureCandles);
        if (structureOk) met.Add(isBull ? "HH/HL structure" : "LH/LL structure");
        else failed.Add(isBull ? "No HH/HL pattern" : "No LH/LL pattern");

        return new DirectionResult(allMandatoryPassed, met.Count, met, failed);
    }

    /// <summary>
    /// Check market structure from recent indicator snapshots.
    /// Bull: higher highs AND higher lows over last N bars.
    /// Bear: lower highs AND lower lows over last N bars.
    /// </summary>
    private static bool EvaluateMarketStructure(bool isBull, IReadOnlyList<IndicatorSnapshot> snapshots, int structureCandles)
    {
        if (snapshots.Count < structureCandles + 1)
            return false;

        // Get the last structureCandles+1 high/low prices to check swing structure
        var recentHighs = snapshots.Skip(snapshots.Count - structureCandles - 1).Select(s => s.MidHigh != 0 ? s.MidHigh : s.CloseMid).ToList();
        var recentLows = snapshots.Skip(snapshots.Count - structureCandles - 1).Select(s => s.MidLow != 0 ? s.MidLow : s.CloseMid).ToList();

        // Simple structure check: compare pairs of local extremes
        // Find local highs and lows over the window
        var highs = new List<decimal>();
        var lows = new List<decimal>();

        for (int i = 1; i < recentHighs.Count - 1; i++)
        {
            if (recentHighs[i] > recentHighs[i - 1] && recentHighs[i] > recentHighs[i + 1])
                highs.Add(recentHighs[i]);
            if (recentLows[i] < recentLows[i - 1] && recentLows[i] < recentLows[i + 1])
                lows.Add(recentLows[i]);
        }

        // Need at least 2 swing points to compare
        if (highs.Count < 2 && lows.Count < 2)
        {
            // Fallback: just check if prices are trending in the right direction
            bool trending = isBull
                ? recentHighs[^1] > recentHighs[0]
                : recentLows[^1] < recentLows[0];
            return trending;
        }

        // B-11: AND instead of OR for stricter structure confirmation
        if (isBull)
        {
            bool hh = highs.Count >= 2 && highs[^1] > highs[^2];
            bool hl = lows.Count >= 2 && lows[^1] > lows[^2];
            return hh && hl;
        }
        else
        {
            bool lh = highs.Count >= 2 && highs[^1] < highs[^2];
            bool ll = lows.Count >= 2 && lows[^1] < lows[^2];
            return lh && ll;
        }
    }

    private static RegimeResult MakeNeutral(string symbol, DateTimeOffset time, int score,
        IReadOnlyList<string> met, IReadOnlyList<string> reasons) =>
        new()
        {
            Symbol = symbol,
            CandleOpenTimeUtc = time,
            Regime = Regime.NEUTRAL,
            RegimeScore = score,
            TriggeredConditions = met,
            DisqualifyingConditions = reasons
        };
}
