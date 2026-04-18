namespace EthSignal.Infrastructure.Engine.Indicators;

/// <summary>
/// Volume Weighted Average Price (rolling window).
/// VWAP_t = sum(TypicalPrice_i * Volume_i) / sum(Volume_i)
/// TypicalPrice = (High + Low + Close) / 3
/// Resets daily at 00:00 UTC.
/// </summary>
public static class VwapCalculator
{
    public static decimal[] Calculate(
        IReadOnlyList<decimal> highs,
        IReadOnlyList<decimal> lows,
        IReadOnlyList<decimal> closes,
        IReadOnlyList<decimal> volumes,
        IReadOnlyList<DateTimeOffset> timestamps)
    {
        int count = closes.Count;
        var result = new decimal[count];

        decimal cumPv = 0;
        decimal cumVol = 0;
        DateOnly currentDay = DateOnly.MinValue;

        for (int i = 0; i < count; i++)
        {
            var day = DateOnly.FromDateTime(timestamps[i].UtcDateTime);

            // Reset on new UTC day
            if (day != currentDay)
            {
                cumPv = 0;
                cumVol = 0;
                currentDay = day;
            }

            decimal tp = (highs[i] + lows[i] + closes[i]) / 3m;
            cumPv += tp * volumes[i];
            cumVol += volumes[i];

            result[i] = cumVol != 0 ? cumPv / cumVol : closes[i];
        }

        return result;
    }
}
