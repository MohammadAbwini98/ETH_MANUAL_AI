namespace EthSignal.Infrastructure.Engine.Indicators;

/// <summary>
/// True Range: TR_t = max(H-L, |H-PrevClose|, |L-PrevClose|)
/// ATR = Wilder smoothing of TR over N periods.
/// </summary>
public static class AtrCalculator
{
    public static decimal[] Calculate(
        IReadOnlyList<decimal> highs,
        IReadOnlyList<decimal> lows,
        IReadOnlyList<decimal> closes,
        int period = 14)
    {
        int count = closes.Count;
        var result = new decimal[count];
        if (count <= period) return result;

        var tr = new decimal[count];
        tr[0] = highs[0] - lows[0]; // No previous close for first bar

        for (int i = 1; i < count; i++)
        {
            decimal hl = highs[i] - lows[i];
            decimal hpc = Math.Abs(highs[i] - closes[i - 1]);
            decimal lpc = Math.Abs(lows[i] - closes[i - 1]);
            tr[i] = Math.Max(hl, Math.Max(hpc, lpc));
        }

        // Initial ATR = average of first 'period' TR values (starting from index 1)
        decimal sum = 0;
        for (int i = 1; i <= period; i++)
            sum += tr[i];
        result[period] = sum / period;

        // Wilder smoothing
        for (int i = period + 1; i < count; i++)
            result[i] = (result[i - 1] * (period - 1) + tr[i]) / period;

        return result;
    }

    /// <summary>Returns raw True Range array (used by ADX calculator).</summary>
    public static decimal[] TrueRange(
        IReadOnlyList<decimal> highs,
        IReadOnlyList<decimal> lows,
        IReadOnlyList<decimal> closes)
    {
        int count = closes.Count;
        var tr = new decimal[count];
        tr[0] = highs[0] - lows[0];
        for (int i = 1; i < count; i++)
        {
            decimal hl = highs[i] - lows[i];
            decimal hpc = Math.Abs(highs[i] - closes[i - 1]);
            decimal lpc = Math.Abs(lows[i] - closes[i - 1]);
            tr[i] = Math.Max(hl, Math.Max(hpc, lpc));
        }
        return tr;
    }
}
