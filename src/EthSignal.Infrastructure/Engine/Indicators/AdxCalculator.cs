namespace EthSignal.Infrastructure.Engine.Indicators;

/// <summary>
/// Directional Movement and ADX.
/// +DM = High_t - High_(t-1) if > (Low_(t-1) - Low_t) and > 0, else 0
/// -DM = Low_(t-1) - Low_t   if > (High_t - High_(t-1)) and > 0, else 0
/// Smoothed +DM/-DM use Wilder smoothing.
/// +DI = 100 * Smoothed(+DM) / ATR
/// -DI = 100 * Smoothed(-DM) / ATR
/// DX = 100 * |+DI - -DI| / (+DI + -DI)
/// ADX = Wilder smoothing of DX over N periods
/// </summary>
public static class AdxCalculator
{
    public record AdxResult(decimal[] Adx, decimal[] PlusDi, decimal[] MinusDi);

    public static AdxResult Calculate(
        IReadOnlyList<decimal> highs,
        IReadOnlyList<decimal> lows,
        IReadOnlyList<decimal> closes,
        int period = 14)
    {
        int count = highs.Count;
        var adx = new decimal[count];
        var plusDi = new decimal[count];
        var minusDi = new decimal[count];

        if (count <= 2 * period) return new AdxResult(adx, plusDi, minusDi);

        // Step 1: Raw +DM, -DM, TR
        var rawPlusDm = new decimal[count];
        var rawMinusDm = new decimal[count];
        var tr = AtrCalculator.TrueRange(highs, lows, closes);

        for (int i = 1; i < count; i++)
        {
            decimal upMove = highs[i] - highs[i - 1];
            decimal downMove = lows[i - 1] - lows[i];

            rawPlusDm[i] = (upMove > downMove && upMove > 0) ? upMove : 0;
            rawMinusDm[i] = (downMove > upMove && downMove > 0) ? downMove : 0;
        }

        // Step 2: Initial smoothed sums (Wilder sum of first 'period' bars, starting at index 1)
        decimal smoothPlusDm = 0, smoothMinusDm = 0, smoothTr = 0;
        for (int i = 1; i <= period; i++)
        {
            smoothPlusDm += rawPlusDm[i];
            smoothMinusDm += rawMinusDm[i];
            smoothTr += tr[i];
        }

        // +DI, -DI at index 'period'
        plusDi[period] = smoothTr != 0 ? 100m * smoothPlusDm / smoothTr : 0;
        minusDi[period] = smoothTr != 0 ? 100m * smoothMinusDm / smoothTr : 0;

        // Step 3: Wilder smooth from period+1 onward, accumulating DX values
        var dx = new decimal[count];
        {
            decimal diSum = plusDi[period] + minusDi[period];
            dx[period] = diSum != 0 ? 100m * Math.Abs(plusDi[period] - minusDi[period]) / diSum : 0;
        }

        for (int i = period + 1; i < count; i++)
        {
            smoothPlusDm = smoothPlusDm - smoothPlusDm / period + rawPlusDm[i];
            smoothMinusDm = smoothMinusDm - smoothMinusDm / period + rawMinusDm[i];
            smoothTr = smoothTr - smoothTr / period + tr[i];

            plusDi[i] = smoothTr != 0 ? 100m * smoothPlusDm / smoothTr : 0;
            minusDi[i] = smoothTr != 0 ? 100m * smoothMinusDm / smoothTr : 0;

            decimal diSum = plusDi[i] + minusDi[i];
            dx[i] = diSum != 0 ? 100m * Math.Abs(plusDi[i] - minusDi[i]) / diSum : 0;
        }

        // Step 4: ADX = Wilder smooth of DX starting at index 2*period
        // Initial ADX = average of DX[period..2*period-1]
        decimal dxSum = 0;
        for (int i = period; i < 2 * period; i++)
            dxSum += dx[i];
        adx[2 * period - 1] = dxSum / period;

        for (int i = 2 * period; i < count; i++)
            adx[i] = (adx[i - 1] * (period - 1) + dx[i]) / period;

        return new AdxResult(adx, plusDi, minusDi);
    }
}
