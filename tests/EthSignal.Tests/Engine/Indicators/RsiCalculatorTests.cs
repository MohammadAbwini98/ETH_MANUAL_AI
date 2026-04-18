using EthSignal.Infrastructure.Engine.Indicators;
using FluentAssertions;

namespace EthSignal.Tests.Engine.Indicators;

/// <summary>P2-T2: RSI verification using known dataset and manually validated results.</summary>
public class RsiCalculatorTests
{
    // Known dataset: 15 closes producing one RSI(14) value at index 14.
    // Closes: 44, 44.34, 44.09, 43.61, 44.33, 44.83, 45.10, 45.42, 45.84, 46.08,
    //         45.89, 46.03, 45.61, 46.28, 46.28
    // Changes: +0.34, -0.25, -0.48, +0.72, +0.50, +0.27, +0.32, +0.42, +0.24,
    //          -0.19, +0.14, -0.42, +0.67, 0.00
    private static readonly decimal[] Closes =
    [
        44m, 44.34m, 44.09m, 43.61m, 44.33m, 44.83m, 45.10m, 45.42m,
        45.84m, 46.08m, 45.89m, 46.03m, 45.61m, 46.28m, 46.28m
    ];

    [Fact]
    public void Rsi14_First_Value_Matches_Reference()
    {
        var rsi = RsiCalculator.Calculate(Closes, 14);

        // Manual calculation:
        // Gains: 0.34, 0, 0, 0.72, 0.50, 0.27, 0.32, 0.42, 0.24, 0, 0.14, 0, 0.67, 0 = 3.62
        // Losses: 0, 0.25, 0.48, 0, 0, 0, 0, 0, 0, 0.19, 0, 0.42, 0, 0 = 1.34
        // AvgGain = 3.62/14 = 0.258571...
        // AvgLoss = 1.34/14 = 0.095714...
        // RS = 0.258571/0.095714 = 2.70149...
        // RSI = 100 - 100/(1+RS) = 72.98...
        decimal sumGain = 0.34m + 0.72m + 0.50m + 0.27m + 0.32m + 0.42m + 0.24m + 0.14m + 0.67m;
        decimal sumLoss = 0.25m + 0.48m + 0.19m + 0.42m;
        decimal avgGain = sumGain / 14m;
        decimal avgLoss = sumLoss / 14m;
        decimal rs = avgGain / avgLoss;
        decimal expectedRsi = 100m - 100m / (1m + rs);

        rsi[14].Should().BeApproximately(expectedRsi, 0.001m);
    }

    [Fact]
    public void Rsi_AllUp_Returns_100()
    {
        // Monotonically increasing: all gains, zero losses
        var prices = Enumerable.Range(0, 20).Select(i => 100m + i).ToArray();
        var rsi = RsiCalculator.Calculate(prices, 14);

        rsi[14].Should().Be(100m);
    }

    [Fact]
    public void Rsi_AllDown_Returns_0()
    {
        // Monotonically decreasing: all losses, zero gains
        var prices = Enumerable.Range(0, 20).Select(i => 120m - i).ToArray();
        var rsi = RsiCalculator.Calculate(prices, 14);

        rsi[14].Should().Be(0m);
    }

    [Fact]
    public void Rsi_Values_Between_0_And_100()
    {
        var prices = new decimal[]
        {
            2000, 2005, 1998, 2010, 2003, 2015, 2008, 2020, 2012, 2025,
            2018, 2030, 2022, 2035, 2028, 2040, 2032, 2045, 2038, 2050
        };
        var rsi = RsiCalculator.Calculate(prices, 14);

        for (int i = 14; i < prices.Length; i++)
        {
            rsi[i].Should().BeGreaterThanOrEqualTo(0m);
            rsi[i].Should().BeLessThanOrEqualTo(100m);
        }
    }

    [Fact]
    public void Rsi_Insufficient_Data_Returns_Zeros()
    {
        var rsi = RsiCalculator.Calculate([1m, 2m, 3m], 14);
        rsi.Should().AllBeEquivalentTo(0m);
    }
}
