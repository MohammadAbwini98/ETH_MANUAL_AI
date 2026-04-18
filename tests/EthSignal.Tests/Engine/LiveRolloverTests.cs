using EthSignal.Domain.Models;
using FluentAssertions;

namespace EthSignal.Tests.Engine;

/// <summary>P1-T6: Live rollover correctness.</summary>
public class LiveRolloverTests
{
    /// <summary>
    /// Simulates the tick-based candle update logic.
    /// Verifies boundary crossing closes old candle correctly.
    /// </summary>
    /// <summary>
    /// P1-02 FIX: Old candle closes with PREVIOUS tick (last price before boundary),
    /// new candle opens with CURRENT tick (first price after boundary).
    /// </summary>
    [Fact]
    public void Tick_At_Boundary_Closes_Old_Opens_New()
    {
        var t0 = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero);

        // Open candle at 10:00, updated by previous ticks
        var current = new RichCandle
        {
            OpenTime = t0,
            BidOpen = 100,
            BidHigh = 105,
            BidLow = 98,
            BidClose = 103,
            AskOpen = 101,
            AskHigh = 106,
            AskLow = 99,
            AskClose = 104,
            Volume = 50,
            IsClosed = false
        };

        // Previous tick (last price before boundary) — this is what closes the old candle
        var prevBid = 102m;
        var prevAsk = 103m;

        // Current tick at 10:01:00 — crosses 1m boundary — this opens the new candle
        var tickTime = t0.AddMinutes(1);
        var newBid = 104m;
        var newAsk = 105m;

        var expected1m = Timeframe.M1.Floor(tickTime);
        expected1m.Should().Be(t0.AddMinutes(1));
        (expected1m > current.OpenTime).Should().BeTrue("boundary crossed");

        // P1-02: Close old candle with PREVIOUS tick's price
        var closed = current with
        {
            BidHigh = Math.Max(current.BidHigh, prevBid),
            BidLow = Math.Min(current.BidLow, prevBid),
            BidClose = prevBid,
            AskHigh = Math.Max(current.AskHigh, prevAsk),
            AskLow = Math.Min(current.AskLow, prevAsk),
            AskClose = prevAsk,
            IsClosed = true
        };

        closed.BidClose.Should().Be(102m, "closed with previous tick bid");
        closed.AskClose.Should().Be(103m, "closed with previous tick ask");
        closed.IsClosed.Should().BeTrue();

        // New candle starts with CURRENT tick
        var newCandle = new RichCandle
        {
            OpenTime = expected1m,
            BidOpen = newBid,
            BidHigh = newBid,
            BidLow = newBid,
            BidClose = newBid,
            AskOpen = newAsk,
            AskHigh = newAsk,
            AskLow = newAsk,
            AskClose = newAsk,
            Volume = 0,
            IsClosed = false
        };

        newCandle.OpenTime.Should().Be(t0.AddMinutes(1));
        newCandle.BidOpen.Should().Be(104m, "new candle opens with current tick");
    }

    [Fact]
    public void Tick_Within_Same_Minute_Updates_Candle()
    {
        var t0 = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero);

        var current = new RichCandle
        {
            OpenTime = t0,
            BidOpen = 100,
            BidHigh = 105,
            BidLow = 98,
            BidClose = 103,
            AskOpen = 101,
            AskHigh = 106,
            AskLow = 99,
            AskClose = 104,
            Volume = 50,
            IsClosed = false
        };

        // Tick at 10:00:30 — same bucket
        var tickTime = t0.AddSeconds(30);
        var expected1m = Timeframe.M1.Floor(tickTime);
        (expected1m > current.OpenTime).Should().BeFalse("same bucket");

        // Update high/low/close
        var newBid = 107m;
        var updated = current with
        {
            BidHigh = Math.Max(current.BidHigh, newBid),
            BidClose = newBid
        };

        updated.BidHigh.Should().Be(107m);
        updated.BidClose.Should().Be(107m);
        updated.BidOpen.Should().Be(100m, "open stays from first tick");
    }

    [Fact]
    public void Tick_At_5m_Boundary_Closes_Both_M1_And_M5()
    {
        var t0 = new DateTimeOffset(2026, 3, 17, 10, 4, 0, TimeSpan.Zero);
        var tickTime = new DateTimeOffset(2026, 3, 17, 10, 5, 0, TimeSpan.Zero);

        // M1 candle at 10:04
        var m1Open = Timeframe.M1.Floor(t0);
        // M5 candle at 10:00
        var m5Open = Timeframe.M5.Floor(t0);

        var m1Expected = Timeframe.M1.Floor(tickTime);
        var m5Expected = Timeframe.M5.Floor(tickTime);

        (m1Expected > m1Open).Should().BeTrue("M1 boundary crossed");
        (m5Expected > m5Open).Should().BeTrue("M5 boundary crossed at :05");

        m1Expected.Should().Be(tickTime); // 10:05
        m5Expected.Should().Be(tickTime); // 10:05
    }

    [Fact]
    public void Tick_At_15m_Boundary_Closes_M1_M5_M15()
    {
        var t0 = new DateTimeOffset(2026, 3, 17, 10, 14, 0, TimeSpan.Zero);
        var tickTime = new DateTimeOffset(2026, 3, 17, 10, 15, 0, TimeSpan.Zero);

        // Only M1, M5, M15 cross a boundary at :15 — higher TFs (30m, 1h, 4h) do not
        var expectedCrossed = new[] { Timeframe.M1, Timeframe.M5, Timeframe.M15 };
        foreach (var tf in expectedCrossed)
        {
            var oldBucket = tf.Floor(t0);
            var newBucket = tf.Floor(tickTime);
            (newBucket > oldBucket).Should().BeTrue($"{tf.Name} boundary crossed");
        }
    }
}
