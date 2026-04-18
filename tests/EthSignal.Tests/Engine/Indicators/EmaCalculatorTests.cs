using EthSignal.Infrastructure.Engine.Indicators;
using FluentAssertions;

namespace EthSignal.Tests.Engine.Indicators;

/// <summary>P2-T1: EMA verification against independently computed reference values.</summary>
public class EmaCalculatorTests
{
    // 10-period EMA on a known dataset.
    // Closes: 22.27, 22.19, 22.08, 22.17, 22.18, 22.13, 22.23, 22.43, 22.24, 22.29, 22.15, 22.39, 22.38, 22.61, 22.45
    // SMA(10) seed = avg(first 10) = 22.221
    // alpha = 2/(10+1) = 0.181818...
    private static readonly decimal[] Closes =
    [
        22.27m, 22.19m, 22.08m, 22.17m, 22.18m,
        22.13m, 22.23m, 22.43m, 22.24m, 22.29m,
        22.15m, 22.39m, 22.38m, 22.61m, 22.45m
    ];

    [Fact]
    public void Ema10_Seed_Is_Sma_Of_First_10()
    {
        var ema = EmaCalculator.Calculate(Closes, 10);

        // SMA(10) = (22.27+22.19+22.08+22.17+22.18+22.13+22.23+22.43+22.24+22.29)/10
        decimal expectedSma = (22.27m + 22.19m + 22.08m + 22.17m + 22.18m +
                               22.13m + 22.23m + 22.43m + 22.24m + 22.29m) / 10m;
        ema[9].Should().Be(expectedSma);
    }

    [Fact]
    public void Ema10_Subsequent_Values_Correct()
    {
        var ema = EmaCalculator.Calculate(Closes, 10);
        decimal alpha = 2m / 11m;

        // EMA[10] = alpha * 22.15 + (1-alpha) * SMA
        decimal sma = (22.27m + 22.19m + 22.08m + 22.17m + 22.18m +
                       22.13m + 22.23m + 22.43m + 22.24m + 22.29m) / 10m;
        decimal expected10 = alpha * 22.15m + (1m - alpha) * sma;
        ema[10].Should().BeApproximately(expected10, 1e-10m);

        // EMA[11] = alpha * 22.39 + (1-alpha) * EMA[10]
        decimal expected11 = alpha * 22.39m + (1m - alpha) * expected10;
        ema[11].Should().BeApproximately(expected11, 1e-10m);
    }

    [Fact]
    public void Ema_Insufficient_Data_Returns_Zeros()
    {
        var ema = EmaCalculator.Calculate([1m, 2m, 3m], 5);
        ema.Should().AllBeEquivalentTo(0m);
    }

    [Fact]
    public void Ema20_And_Ema50_Produce_Values()
    {
        // Generate 60 prices
        var prices = Enumerable.Range(0, 60).Select(i => 2000m + i * 0.5m).ToArray();
        var ema20 = EmaCalculator.Calculate(prices, 20);
        var ema50 = EmaCalculator.Calculate(prices, 50);

        ema20[19].Should().BeGreaterThan(0, "EMA20 should have value at index 19");
        ema50[49].Should().BeGreaterThan(0, "EMA50 should have value at index 49");

        // In an uptrend, EMA20 > EMA50
        ema20[59].Should().BeGreaterThan(ema50[59]);
    }

    [Fact]
    public void Ema_Constant_Input_Returns_That_Constant()
    {
        var prices = Enumerable.Repeat(100m, 30).ToArray();
        var ema = EmaCalculator.Calculate(prices, 10);
        for (int i = 9; i < 30; i++)
            ema[i].Should().BeApproximately(100m, 1e-10m);
    }
}
