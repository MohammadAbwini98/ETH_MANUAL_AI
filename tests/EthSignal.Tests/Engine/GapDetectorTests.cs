using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Engine;
using FluentAssertions;

namespace EthSignal.Tests.Engine;

public class GapDetectorTests
{
    [Fact]
    public void NoGaps_ReturnsEmpty()
    {
        var from = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero);
        var to = from.AddMinutes(5);
        var actual = new List<DateTimeOffset>
        {
            from, from.AddMinutes(1), from.AddMinutes(2), from.AddMinutes(3), from.AddMinutes(4)
        };

        var gaps = GapDetector.DetectGaps("ETHUSD", Timeframe.M1, from, to, actual);
        gaps.Should().BeEmpty();
    }

    [Fact]
    public void SingleMissingCandle_DetectsGap()
    {
        var from = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero);
        var to = from.AddMinutes(5);
        // Missing minute 2
        var actual = new List<DateTimeOffset>
        {
            from, from.AddMinutes(1), from.AddMinutes(3), from.AddMinutes(4)
        };

        var gaps = GapDetector.DetectGaps("ETHUSD", Timeframe.M1, from, to, actual);
        gaps.Should().HaveCount(1);
        gaps[0].ExpectedTime.Should().Be(from.AddMinutes(2));
        gaps[0].ActualNextTime.Should().Be(from.AddMinutes(3));
        gaps[0].GapType.Should().Be("missing_candle");
    }

    [Fact]
    public void MultipleGaps_AllDetected()
    {
        var from = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero);
        var to = from.AddMinutes(10);
        // Only have minutes 0, 3, 7
        var actual = new List<DateTimeOffset>
        {
            from, from.AddMinutes(3), from.AddMinutes(7)
        };

        var gaps = GapDetector.DetectGaps("ETHUSD", Timeframe.M1, from, to, actual);
        gaps.Should().HaveCount(7); // minutes 1,2,4,5,6,8,9
    }

    [Fact]
    public void DefaultSource_IsLive()
    {
        var from = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero);
        var to = from.AddMinutes(5);
        var actual = new List<DateTimeOffset> { from, from.AddMinutes(1), from.AddMinutes(3), from.AddMinutes(4) };

        var gaps = GapDetector.DetectGaps("ETHUSD", Timeframe.M1, from, to, actual);
        gaps.Should().HaveCount(1);
        gaps[0].GapSource.Should().Be("LIVE");
    }

    [Fact]
    public void BackfillSource_IsBackfill()
    {
        var from = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero);
        var to = from.AddMinutes(5);
        var actual = new List<DateTimeOffset> { from, from.AddMinutes(1), from.AddMinutes(3), from.AddMinutes(4) };

        var gaps = GapDetector.DetectGaps("ETHUSD", Timeframe.M1, from, to, actual, gapSource: "BACKFILL");
        gaps.Should().HaveCount(1);
        gaps[0].GapSource.Should().Be("BACKFILL");
    }

    [Fact]
    public void BackfillEndTime_ShouldFloorToClosedMinute()
    {
        // REQ-NS-003: Verify Timeframe.M1.Floor works as expected
        var now = new DateTimeOffset(2026, 3, 30, 14, 23, 45, TimeSpan.Zero);
        var floored = Timeframe.M1.Floor(now);
        floored.Should().Be(new DateTimeOffset(2026, 3, 30, 14, 23, 0, TimeSpan.Zero));
    }
}
