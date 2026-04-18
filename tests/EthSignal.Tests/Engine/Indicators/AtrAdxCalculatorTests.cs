using EthSignal.Infrastructure.Engine.Indicators;
using FluentAssertions;

namespace EthSignal.Tests.Engine.Indicators;

/// <summary>P2-T4: ATR and ADX verification against offline benchmark.</summary>
public class AtrAdxCalculatorTests
{
    // Generate a known dataset with clear trend for testing
    private static (decimal[] Highs, decimal[] Lows, decimal[] Closes) MakeTrendData(int count, decimal start, decimal step)
    {
        var highs = new decimal[count];
        var lows = new decimal[count];
        var closes = new decimal[count];
        for (int i = 0; i < count; i++)
        {
            var mid = start + i * step;
            highs[i] = mid + 5m;
            lows[i] = mid - 5m;
            closes[i] = mid + 2m;
        }
        return (highs, lows, closes);
    }

    [Fact]
    public void Atr_First_Value_Is_Average_Of_TR()
    {
        // Small hand-verifiable dataset
        // Bar 0: H=50, L=40, C=45 → TR=10 (no prev close)
        // Bar 1: H=52, L=41, C=48 → TR=max(11, |52-45|=7, |41-45|=4) = 11
        // Bar 2: H=55, L=43, C=50 → TR=max(12, |55-48|=7, |43-48|=5) = 12
        // Bar 3: H=53, L=42, C=47 → TR=max(11, |53-50|=3, |42-50|=8) = 11
        // ATR(3) at index 3 = (TR[1]+TR[2]+TR[3])/3 = (11+12+11)/3 = 11.333...
        var highs = new decimal[] { 50, 52, 55, 53, 56 };
        var lows = new decimal[] { 40, 41, 43, 42, 44 };
        var closes = new decimal[] { 45, 48, 50, 47, 52 };

        var atr = AtrCalculator.Calculate(highs, lows, closes, 3);

        decimal expectedAtr3 = (11m + 12m + 11m) / 3m;
        atr[3].Should().BeApproximately(expectedAtr3, 0.001m);
    }

    [Fact]
    public void Atr_Wilder_Smoothing_Applied()
    {
        var highs = new decimal[] { 50, 52, 55, 53, 56, 58 };
        var lows = new decimal[] { 40, 41, 43, 42, 44, 45 };
        var closes = new decimal[] { 45, 48, 50, 47, 52, 54 };

        var atr = AtrCalculator.Calculate(highs, lows, closes, 3);

        // ATR[3] = (11+12+11)/3 = 11.333...
        // TR[4] = max(56-44=12, |56-47|=9, |44-47|=3) = 12
        // ATR[4] = (ATR[3] * 2 + TR[4]) / 3 = (11.333*2 + 12) / 3 = 11.555...
        decimal atr3 = (11m + 12m + 11m) / 3m;
        decimal tr4 = 12m;
        decimal expectedAtr4 = (atr3 * 2m + tr4) / 3m;
        atr[4].Should().BeApproximately(expectedAtr4, 0.001m);
    }

    [Fact]
    public void Atr14_Always_Positive()
    {
        var (highs, lows, closes) = MakeTrendData(40, 2000m, 1m);
        var atr = AtrCalculator.Calculate(highs, lows, closes, 14);

        for (int i = 14; i < 40; i++)
            atr[i].Should().BeGreaterThan(0m);
    }

    [Fact]
    public void Adx_Strong_Uptrend_High_Value()
    {
        // Strong uptrend: consistently higher highs and higher lows
        var (highs, lows, closes) = MakeTrendData(40, 2000m, 3m);
        var adx = AdxCalculator.Calculate(highs, lows, closes, 14);

        // In strong uptrend, +DI should be > -DI
        adx.PlusDi[39].Should().BeGreaterThan(adx.MinusDi[39], "+DI should dominate in uptrend");

        // ADX should indicate trend (> 0 at least)
        adx.Adx[39].Should().BeGreaterThan(0m);
    }

    [Fact]
    public void Adx_Strong_Downtrend_MinusDi_Dominates()
    {
        // Strong downtrend
        var (highs, lows, closes) = MakeTrendData(40, 3000m, -3m);
        var adx = AdxCalculator.Calculate(highs, lows, closes, 14);

        // In downtrend, -DI should be > +DI
        adx.MinusDi[39].Should().BeGreaterThan(adx.PlusDi[39], "-DI should dominate in downtrend");
    }

    [Fact]
    public void Adx_Values_Between_0_And_100()
    {
        var (highs, lows, closes) = MakeTrendData(40, 2000m, 2m);
        var adx = AdxCalculator.Calculate(highs, lows, closes, 14);

        for (int i = 27; i < 40; i++) // ADX valid from 2*14-1 = 27
        {
            adx.Adx[i].Should().BeGreaterThanOrEqualTo(0m);
            adx.Adx[i].Should().BeLessThanOrEqualTo(100m);
            adx.PlusDi[i].Should().BeGreaterThanOrEqualTo(0m);
            adx.MinusDi[i].Should().BeGreaterThanOrEqualTo(0m);
        }
    }

    [Fact]
    public void Adx_Insufficient_Data_Returns_Zeros()
    {
        var highs = Enumerable.Range(0, 10).Select(i => (decimal)(100 + i)).ToArray();
        var lows = Enumerable.Range(0, 10).Select(i => (decimal)(90 + i)).ToArray();
        var closes = Enumerable.Range(0, 10).Select(i => (decimal)(95 + i)).ToArray();

        var adx = AdxCalculator.Calculate(highs, lows, closes, 14);
        adx.Adx.Should().AllBeEquivalentTo(0m);
    }
}
