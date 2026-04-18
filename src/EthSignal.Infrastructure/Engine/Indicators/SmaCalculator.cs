namespace EthSignal.Infrastructure.Engine.Indicators;

/// <summary>
/// Simple Moving Average over N periods.
/// SMA_t = sum(Value_(t-N+1) ... Value_t) / N
/// </summary>
public static class SmaCalculator
{
    public static decimal[] Calculate(IReadOnlyList<decimal> values, int period)
    {
        int count = values.Count;
        var result = new decimal[count];
        if (count < period) return result;

        decimal sum = 0;
        for (int i = 0; i < period; i++)
            sum += values[i];
        result[period - 1] = sum / period;

        for (int i = period; i < count; i++)
        {
            sum += values[i] - values[i - period];
            result[i] = sum / period;
        }

        return result;
    }
}
