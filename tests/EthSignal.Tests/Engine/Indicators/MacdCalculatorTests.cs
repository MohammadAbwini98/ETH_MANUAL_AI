using EthSignal.Infrastructure.Engine.Indicators;
using FluentAssertions;

namespace EthSignal.Tests.Engine.Indicators;

/// <summary>P2-T3: MACD verification against reference implementation.</summary>
public class MacdCalculatorTests
{
    [Fact]
    public void Macd_Equals_Ema12_Minus_Ema26()
    {
        // Generate 40 prices
        var prices = Enumerable.Range(0, 40)
            .Select(i => 2000m + (decimal)Math.Sin(i * 0.3) * 10m)
            .ToArray();

        var macd = MacdCalculator.Calculate(prices, 12, 26, 9);
        var ema12 = EmaCalculator.Calculate(prices, 12);
        var ema26 = EmaCalculator.Calculate(prices, 26);

        // MACD line should equal EMA12 - EMA26 from index 25 onward
        for (int i = 25; i < prices.Length; i++)
        {
            decimal expected = ema12[i] - ema26[i];
            macd.Macd[i].Should().BeApproximately(expected, 1e-10m,
                $"MACD at index {i} should be EMA12-EMA26");
        }
    }

    [Fact]
    public void Macd_Histogram_Equals_Macd_Minus_Signal()
    {
        var prices = Enumerable.Range(0, 50)
            .Select(i => 2000m + i * 0.5m + (decimal)Math.Sin(i * 0.5) * 5m)
            .ToArray();

        var macd = MacdCalculator.Calculate(prices, 12, 26, 9);

        // From index where signal is valid (26-1 + 9-1 = 33)
        for (int i = 33; i < prices.Length; i++)
        {
            decimal expected = macd.Macd[i] - macd.Signal[i];
            macd.Histogram[i].Should().BeApproximately(expected, 1e-10m,
                $"Histogram at index {i} should be MACD-Signal");
        }
    }

    [Fact]
    public void Macd_Uptrend_Positive()
    {
        // Strong uptrend: EMA12 should be above EMA26 → positive MACD
        var prices = Enumerable.Range(0, 50)
            .Select(i => 2000m + i * 2m)
            .ToArray();

        var macd = MacdCalculator.Calculate(prices, 12, 26, 9);

        // After warm-up, MACD should be positive in uptrend
        macd.Macd[49].Should().BeGreaterThan(0m);
    }

    [Fact]
    public void Macd_Constant_Price_Converges_To_Zero()
    {
        var prices = Enumerable.Repeat(1500m, 60).ToArray();
        var macd = MacdCalculator.Calculate(prices, 12, 26, 9);

        // With constant prices, all EMAs converge to the same value → MACD = 0
        macd.Macd[59].Should().BeApproximately(0m, 0.001m);
        macd.Signal[59].Should().BeApproximately(0m, 0.001m);
        macd.Histogram[59].Should().BeApproximately(0m, 0.001m);
    }

    [Fact]
    public void Macd_Insufficient_Data_Returns_Zeros()
    {
        var prices = Enumerable.Range(0, 10).Select(i => (decimal)i).ToArray();
        var macd = MacdCalculator.Calculate(prices, 12, 26, 9);
        macd.Macd.Should().AllBeEquivalentTo(0m);
    }
}
