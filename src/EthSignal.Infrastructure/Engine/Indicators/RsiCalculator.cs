namespace EthSignal.Infrastructure.Engine.Indicators;

/// <summary>
/// Relative Strength Index using Wilder smoothing.
/// Gain_t = max(Close_t - Close_(t-1), 0)
/// Loss_t = max(Close_(t-1) - Close_t, 0)
/// AvgGain/AvgLoss use Wilder smoothing: prev * (N-1)/N + current / N
/// RS = AvgGain / AvgLoss
/// RSI = 100 - 100/(1 + RS)
/// If AvgLoss = 0, RSI = 100.
/// </summary>
public static class RsiCalculator
{
    public static decimal[] Calculate(IReadOnlyList<decimal> closes, int period = 14)
    {
        var result = new decimal[closes.Count];
        if (closes.Count <= period) return result;

        // Calculate initial gains/losses
        decimal sumGain = 0, sumLoss = 0;
        for (int i = 1; i <= period; i++)
        {
            var change = closes[i] - closes[i - 1];
            if (change > 0) sumGain += change;
            else sumLoss += -change;
        }

        decimal avgGain = sumGain / period;
        decimal avgLoss = sumLoss / period;

        result[period] = avgLoss == 0 ? 100m : 100m - 100m / (1m + avgGain / avgLoss);

        // Wilder smoothing for subsequent values
        for (int i = period + 1; i < closes.Count; i++)
        {
            var change = closes[i] - closes[i - 1];
            decimal gain = change > 0 ? change : 0;
            decimal loss = change < 0 ? -change : 0;

            avgGain = (avgGain * (period - 1) + gain) / period;
            avgLoss = (avgLoss * (period - 1) + loss) / period;

            result[i] = avgLoss == 0 ? 100m : 100m - 100m / (1m + avgGain / avgLoss);
        }

        return result;
    }
}
