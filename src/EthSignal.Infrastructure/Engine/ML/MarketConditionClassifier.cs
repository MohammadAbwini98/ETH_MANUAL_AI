using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Engine.ML;

/// <summary>
/// Classifies current market conditions into a composite <see cref="MarketConditionClass"/>
/// from observable indicators. All thresholds are relative (ratios) to avoid hardcoding
/// absolute price levels.
/// </summary>
public static class MarketConditionClassifier
{
    public static MarketConditionClass Classify(
        IndicatorSnapshot snap,
        RegimeResult? regime,
        RichCandle candle,
        decimal atrSma50,
        StrategyParameters p)
    {
        return new MarketConditionClass(
            ClassifyVolatility(snap.Atr14, atrSma50),
            ClassifyTrend(snap.Adx14, regime?.RegimeScore ?? 0),
            ClassifySession(candle.OpenTime),
            ClassifySpread(snap, p),
            ClassifyVolume(candle, snap));
    }

    // ─── Volatility (ATR14 vs 50-bar ATR SMA) ──────────

    internal static VolatilityTier ClassifyVolatility(decimal atr14, decimal atrSma50)
    {
        if (atrSma50 <= 0) return VolatilityTier.NORMAL;

        var ratio = atr14 / atrSma50;
        return ratio switch
        {
            > 2.5m => VolatilityTier.EXTREME,
            > 1.5m => VolatilityTier.HIGH,
            < 0.6m => VolatilityTier.LOW,
            _ => VolatilityTier.NORMAL
        };
    }

    // ─── Trend Strength (ADX14 + Regime Score) ──────────

    internal static TrendStrength ClassifyTrend(decimal adx14, int regimeScore)
    {
        if (adx14 >= 30 && regimeScore >= 4) return TrendStrength.STRONG;
        if (adx14 < 18 || regimeScore <= 2) return TrendStrength.WEAK;
        return TrendStrength.MODERATE;
    }

    // ─── Trading Session (candle UTC hour) ──────────────

    internal static TradingSession ClassifySession(DateTimeOffset candleTime)
    {
        var hour = candleTime.UtcDateTime.Hour;
        return hour switch
        {
            >= 22 or < 7 => TradingSession.ASIA,
            >= 7 and < 13 => TradingSession.LONDON,
            >= 13 and < 17 => TradingSession.OVERLAP,
            _ => TradingSession.NEW_YORK // 17–22
        };
    }

    // ─── Spread Quality (spread % vs MaxSpreadPct) ─────

    internal static SpreadQuality ClassifySpread(IndicatorSnapshot snap, StrategyParameters p)
    {
        if (snap.CloseMid <= 0 || p.MaxSpreadPct <= 0) return SpreadQuality.NORMAL;

        var spreadPct = snap.Spread / snap.CloseMid;
        if (spreadPct >= 0.85m * p.MaxSpreadPct) return SpreadQuality.WIDE;
        if (spreadPct < 0.50m * p.MaxSpreadPct) return SpreadQuality.TIGHT;
        return SpreadQuality.NORMAL;
    }

    // ─── Volume Tier (candle volume vs SMA20) ───────────

    internal static VolumeTier ClassifyVolume(RichCandle candle, IndicatorSnapshot snap)
    {
        if (snap.VolumeSma20 <= 0) return VolumeTier.NORMAL;

        var ratio = candle.Volume / snap.VolumeSma20;
        if (ratio >= 1.5m) return VolumeTier.ACTIVE;
        if (ratio < 0.4m) return VolumeTier.DRY;
        return VolumeTier.NORMAL;
    }

    /// <summary>
    /// Compute ATR SMA50 from the snapshot buffer. Returns 0 if insufficient data.
    /// </summary>
    public static decimal ComputeAtrSma50(IReadOnlyList<IndicatorSnapshot> snapshots)
    {
        if (snapshots.Count < 10) return 0m; // need reasonable history

        var count = Math.Min(50, snapshots.Count);
        var sum = 0m;
        for (var i = 0; i < count; i++)
            sum += snapshots[i].Atr14;
        return sum / count;
    }
}
