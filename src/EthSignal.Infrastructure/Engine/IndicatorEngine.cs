using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Engine.Indicators;

namespace EthSignal.Infrastructure.Engine;

/// <summary>
/// Orchestrates indicator computation on a series of closed candles.
/// Produces one IndicatorSnapshot per candle (only valid after warm-up period).
/// </summary>
public static class IndicatorEngine
{
    /// <summary>
    /// Legacy constant — kept for backward compatibility. Use StrategyParameters.WarmUpPeriod instead.
    /// </summary>
    public const int WarmUpPeriod = 50;

    /// <summary>
    /// Compute indicators for all candles using default parameters.
    /// </summary>
    public static IReadOnlyList<IndicatorSnapshot> ComputeAll(
        string symbol, string timeframe, IReadOnlyList<RichCandle> candles)
        => ComputeAll(symbol, timeframe, candles, StrategyParameters.Default);

    /// <summary>
    /// Compute indicators for all candles using the given parameter set.
    /// Only candles at index >= warmUpPeriod-1 produce fully valid snapshots.
    /// </summary>
    public static IReadOnlyList<IndicatorSnapshot> ComputeAll(
        string symbol, string timeframe, IReadOnlyList<RichCandle> candles, StrategyParameters p)
    {
        if (candles.Count == 0) return [];

        // Extract price arrays (mid prices for indicators per SRS)
        var midCloses = candles.Select(c => c.MidClose).ToArray();
        var midHighs = candles.Select(c => c.MidHigh).ToArray();
        var midLows = candles.Select(c => c.MidLow).ToArray();
        var volumes = candles.Select(c => c.Volume).ToArray();
        var timestamps = candles.Select(c => c.OpenTime).ToArray();
        var bidCloses = candles.Select(c => c.BidClose).ToArray();
        var askCloses = candles.Select(c => c.AskClose).ToArray();

        // Compute all indicator series from parameterized periods
        var emaFast = EmaCalculator.Calculate(midCloses, p.EmaFastPeriod);
        var emaSlow = EmaCalculator.Calculate(midCloses, p.EmaSlowPeriod);
        var rsi = RsiCalculator.Calculate(midCloses, p.RsiPeriod);
        var macd = MacdCalculator.Calculate(midCloses, p.MacdFast, p.MacdSlow, p.MacdSignalPeriod);
        var atr = AtrCalculator.Calculate(midHighs, midLows, midCloses, p.AtrPeriod);
        var adxResult = AdxCalculator.Calculate(midHighs, midLows, midCloses, p.AdxPeriod);
        var volSma = SmaCalculator.Calculate(volumes, p.VolumeLookback);
        var vwap = VwapCalculator.Calculate(midHighs, midLows, midCloses, volumes, timestamps);
        var spread = SpreadCalculator.Calculate(bidCloses, askCloses);

        var snapshots = new List<IndicatorSnapshot>(candles.Count);

        for (int i = 0; i < candles.Count; i++)
        {
            var c = candles[i];
            bool provisional = i < p.WarmUpPeriod - 1 || !c.IsClosed;

            snapshots.Add(new IndicatorSnapshot
            {
                Symbol = symbol,
                Timeframe = timeframe,
                CandleOpenTimeUtc = c.OpenTime,
                Ema20 = emaFast[i],
                Ema50 = emaSlow[i],
                Rsi14 = rsi[i],
                Macd = macd.Macd[i],
                MacdSignal = macd.Signal[i],
                MacdHist = macd.Histogram[i],
                Atr14 = atr[i],
                Adx14 = adxResult.Adx[i],
                PlusDi = adxResult.PlusDi[i],
                MinusDi = adxResult.MinusDi[i],
                VolumeSma20 = volSma[i],
                Vwap = vwap[i],
                Spread = spread[i],
                CloseMid = c.MidClose,
                MidHigh = c.MidHigh,
                MidLow = c.MidLow,
                IsProvisional = provisional
            });
        }

        return snapshots;
    }

    /// <summary>
    /// Compute indicator for only the latest candle using default parameters.
    /// </summary>
    public static IndicatorSnapshot? ComputeLatest(
        string symbol, string timeframe, IReadOnlyList<RichCandle> candles)
        => ComputeLatest(symbol, timeframe, candles, StrategyParameters.Default);

    /// <summary>
    /// Compute indicator for only the latest candle using the given parameter set.
    /// Returns null if insufficient history.
    /// </summary>
    public static IndicatorSnapshot? ComputeLatest(
        string symbol, string timeframe, IReadOnlyList<RichCandle> candles, StrategyParameters p)
    {
        if (candles.Count == 0) return null;
        var all = ComputeAll(symbol, timeframe, candles, p);
        return all[^1];
    }
}
