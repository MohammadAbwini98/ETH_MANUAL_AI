namespace EthSignal.Infrastructure.Engine.Indicators;

/// <summary>
/// MACD = EMA(12) - EMA(26)
/// Signal = EMA(9) of MACD
/// Histogram = MACD - Signal
/// </summary>
public static class MacdCalculator
{
    public record MacdResult(decimal[] Macd, decimal[] Signal, decimal[] Histogram);

    public static MacdResult Calculate(IReadOnlyList<decimal> closes, int fast = 12, int slow = 26, int signal = 9)
    {
        var emaFast = EmaCalculator.Calculate(closes, fast);
        var emaSlow = EmaCalculator.Calculate(closes, slow);

        var macdLine = new decimal[closes.Count];

        // MACD line valid from index slow-1 onward (where both EMAs are valid)
        for (int i = slow - 1; i < closes.Count; i++)
            macdLine[i] = emaFast[i] - emaSlow[i];

        // Signal line = EMA(9) of MACD values starting from index slow-1
        var macdValues = new List<decimal>();
        for (int i = slow - 1; i < closes.Count; i++)
            macdValues.Add(macdLine[i]);

        var signalOnMacd = EmaCalculator.Calculate(macdValues, signal);

        var signalLine = new decimal[closes.Count];
        var histogram = new decimal[closes.Count];

        // Map signal back to original indices
        int offset = slow - 1;
        for (int i = 0; i < signalOnMacd.Length; i++)
        {
            signalLine[offset + i] = signalOnMacd[i];
            histogram[offset + i] = macdLine[offset + i] - signalOnMacd[i];
        }

        return new MacdResult(macdLine, signalLine, histogram);
    }
}
