using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Engine;
using FluentAssertions;

namespace EthSignal.Tests.Engine;

/// <summary>P1-T5: Aggregation accuracy.</summary>
public class AggregationTests
{
    private static RichCandle Make1m(DateTimeOffset time, decimal bidO, decimal bidH, decimal bidL, decimal bidC,
        decimal askO, decimal askH, decimal askL, decimal askC, decimal vol = 100m)
        => new()
        {
            OpenTime = time,
            BidOpen = bidO, BidHigh = bidH, BidLow = bidL, BidClose = bidC,
            AskOpen = askO, AskHigh = askH, AskLow = askL, AskClose = askC,
            Volume = vol
        };

    [Fact]
    public void Aggregate_5m_From_Five_1m_Candles()
    {
        var t0 = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero);
        var candles = new List<RichCandle>
        {
            Make1m(t0,                     100, 105, 98, 103,   101, 106, 99, 104, 10),
            Make1m(t0.AddMinutes(1),       103, 108, 101, 106,  104, 109, 102, 107, 20),
            Make1m(t0.AddMinutes(2),       106, 110, 104, 107,  107, 111, 105, 108, 30),
            Make1m(t0.AddMinutes(3),       107, 109, 103, 105,  108, 110, 104, 106, 15),
            Make1m(t0.AddMinutes(4),       105, 107, 102, 104,  106, 108, 103, 105, 25),
        };

        var result = CandleAggregator.Aggregate(candles, Timeframe.M5);

        result.Should().HaveCount(1);
        var agg = result[0];

        // Open = first, Close = last
        agg.OpenTime.Should().Be(t0);
        agg.BidOpen.Should().Be(100m);
        agg.BidClose.Should().Be(104m);
        agg.AskOpen.Should().Be(101m);
        agg.AskClose.Should().Be(105m);

        // High = max
        agg.BidHigh.Should().Be(110m);
        agg.AskHigh.Should().Be(111m);

        // Low = min
        agg.BidLow.Should().Be(98m);
        agg.AskLow.Should().Be(99m);

        // Volume = sum
        agg.Volume.Should().Be(100m);

        // Mid computed correctly
        agg.MidOpen.Should().Be((100m + 101m) / 2m);
        agg.MidHigh.Should().Be((110m + 111m) / 2m);
        agg.MidLow.Should().Be((98m + 99m) / 2m);
        agg.MidClose.Should().Be((104m + 105m) / 2m);
    }

    [Fact]
    public void Aggregate_15m_From_Fifteen_1m_Candles()
    {
        var t0 = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero);
        var candles = Enumerable.Range(0, 15).Select(i =>
            Make1m(t0.AddMinutes(i), 100 + i, 105 + i, 95 + i, 102 + i,
                101 + i, 106 + i, 96 + i, 103 + i, 10m)).ToList();

        var result = CandleAggregator.Aggregate(candles, Timeframe.M15);

        result.Should().HaveCount(1);
        var agg = result[0];

        agg.BidOpen.Should().Be(100m);   // first candle open
        agg.BidClose.Should().Be(116m);  // last candle close (102+14)
        agg.BidHigh.Should().Be(119m);   // max(105+i) = 105+14
        agg.BidLow.Should().Be(95m);     // min(95+i) = 95+0
        agg.Volume.Should().Be(150m);    // 15 * 10
    }

    [Fact]
    public void Aggregate_Empty_Input_Returns_Empty()
    {
        var result = CandleAggregator.Aggregate([], Timeframe.M5);
        result.Should().BeEmpty();
    }

    [Fact]
    public void Aggregate_SingleCandle_ReturnsSelf()
    {
        var t0 = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero);
        var candle = Make1m(t0, 100, 105, 95, 102, 101, 106, 96, 103);

        var result = CandleAggregator.Aggregate([candle], Timeframe.M5);

        result.Should().HaveCount(1);
        result[0].BidOpen.Should().Be(100m);
        result[0].Volume.Should().Be(100m);
    }

    [Fact]
    public void Aggregate_M1_Returns_Input_As_Is()
    {
        var t0 = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero);
        var candles = new List<RichCandle>
        {
            Make1m(t0, 100, 105, 95, 102, 101, 106, 96, 103),
            Make1m(t0.AddMinutes(1), 102, 107, 97, 104, 103, 108, 98, 105),
        };

        var result = CandleAggregator.Aggregate(candles, Timeframe.M1);
        result.Should().HaveCount(2);
    }

    [Fact]
    public void Aggregate_Incomplete_Period_StillWorks()
    {
        var t0 = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero);
        // Only 3 candles for a 5m period
        var candles = Enumerable.Range(0, 3).Select(i =>
            Make1m(t0.AddMinutes(i), 100, 105, 95, 102, 101, 106, 96, 103)).ToList();

        var result = CandleAggregator.Aggregate(candles, Timeframe.M5);
        result.Should().HaveCount(1);
        result[0].Volume.Should().Be(300m); // 3 * 100
    }

    [Fact]
    public void Aggregate_CrossesBucketBoundary()
    {
        var t0 = new DateTimeOffset(2026, 3, 17, 10, 3, 0, TimeSpan.Zero); // minute 3
        var t1 = new DateTimeOffset(2026, 3, 17, 10, 6, 0, TimeSpan.Zero); // minute 6 (next 5m bucket)

        var candles = new List<RichCandle>
        {
            Make1m(t0, 100, 105, 95, 102, 101, 106, 96, 103, 50),
            Make1m(t1, 110, 115, 105, 112, 111, 116, 106, 113, 60),
        };

        var result = CandleAggregator.Aggregate(candles, Timeframe.M5);
        result.Should().HaveCount(2); // Two different 5m buckets
    }
}
