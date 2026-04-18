using EthSignal.Infrastructure.Engine.Indicators;
using FluentAssertions;

namespace EthSignal.Tests.Engine.Indicators;

/// <summary>VWAP and Spread tests.</summary>
public class VwapSpreadTests
{
    [Fact]
    public void Vwap_Simple_Calculation()
    {
        var highs = new decimal[] { 105, 110, 108 };
        var lows = new decimal[] { 95, 100, 98 };
        var closes = new decimal[] { 102, 107, 104 };
        var volumes = new decimal[] { 1000, 2000, 1500 };
        var timestamps = new DateTimeOffset[]
        {
            new(2026, 3, 17, 10, 0, 0, TimeSpan.Zero),
            new(2026, 3, 17, 10, 5, 0, TimeSpan.Zero),
            new(2026, 3, 17, 10, 10, 0, TimeSpan.Zero),
        };

        var vwap = VwapCalculator.Calculate(highs, lows, closes, volumes, timestamps);

        // Bar 0: TP=(105+95+102)/3=100.6667, cumPV=100666.7, cumVol=1000 → VWAP=100.6667
        decimal tp0 = (105m + 95m + 102m) / 3m;
        vwap[0].Should().BeApproximately(tp0, 0.001m);

        // Bar 1: TP=(110+100+107)/3=105.6667, cumPV=100666.7+211333.4=312000.1, cumVol=3000
        decimal tp1 = (110m + 100m + 107m) / 3m;
        decimal expectedVwap1 = (tp0 * 1000m + tp1 * 2000m) / 3000m;
        vwap[1].Should().BeApproximately(expectedVwap1, 0.001m);
    }

    [Fact]
    public void Vwap_Resets_On_New_Day()
    {
        var highs = new decimal[] { 105, 110 };
        var lows = new decimal[] { 95, 100 };
        var closes = new decimal[] { 102, 107 };
        var volumes = new decimal[] { 1000, 2000 };
        var timestamps = new DateTimeOffset[]
        {
            new(2026, 3, 17, 23, 55, 0, TimeSpan.Zero), // Day 1
            new(2026, 3, 18, 0, 0, 0, TimeSpan.Zero),   // Day 2 (reset)
        };

        var vwap = VwapCalculator.Calculate(highs, lows, closes, volumes, timestamps);

        // Bar 1 should be reset (only its own TP)
        decimal tp1 = (110m + 100m + 107m) / 3m;
        vwap[1].Should().BeApproximately(tp1, 0.001m);
    }

    [Fact]
    public void Spread_Equals_Ask_Minus_Bid()
    {
        var bids = new decimal[] { 2000, 2005, 2010 };
        var asks = new decimal[] { 2001, 2006, 2012 };

        var spread = SpreadCalculator.Calculate(bids, asks);

        spread[0].Should().Be(1m);
        spread[1].Should().Be(1m);
        spread[2].Should().Be(2m);
    }
}
