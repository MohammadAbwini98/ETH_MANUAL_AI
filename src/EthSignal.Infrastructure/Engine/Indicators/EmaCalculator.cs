namespace EthSignal.Infrastructure.Engine.Indicators;

/// <summary>
/// Exponential Moving Average.
/// alpha = 2 / (N + 1)
/// EMA_t = alpha * Price_t + (1 - alpha) * EMA_(t-1)
/// Seed: SMA of first N values.
/// </summary>
public static class EmaCalculator
{
    /// <summary>
    /// Computes EMA series for given close prices.
    /// Returns array of same length; entries before period are NaN-equivalent (0).
    /// </summary>
    public static decimal[] Calculate(IReadOnlyList<decimal> closes, int period)
    {
        var result = new decimal[closes.Count];
        if (closes.Count < period) return result;

        // Seed with SMA of first 'period' values
        decimal sum = 0;
        for (int i = 0; i < period; i++)
            sum += closes[i];
        decimal sma = sum / period;
        result[period - 1] = sma;

        decimal alpha = 2m / (period + 1);

        for (int i = period; i < closes.Count; i++)
            result[i] = alpha * closes[i] + (1m - alpha) * result[i - 1];

        return result;
    }
}
