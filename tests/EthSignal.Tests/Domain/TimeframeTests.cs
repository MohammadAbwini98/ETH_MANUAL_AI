using EthSignal.Domain.Models;
using FluentAssertions;

namespace EthSignal.Tests.Domain;

/// <summary>P1-T4: Time bucket correctness.</summary>
public class TimeframeTests
{
    [Fact]
    public void All_ContainsAllSixTimeframes()
    {
        Timeframe.All.Should().HaveCount(6);
        Timeframe.All.Should().Contain(Timeframe.M1);
        Timeframe.All.Should().Contain(Timeframe.M5);
        Timeframe.All.Should().Contain(Timeframe.M15);
        Timeframe.All.Should().Contain(Timeframe.M30);
        Timeframe.All.Should().Contain(Timeframe.H1);
        Timeframe.All.Should().Contain(Timeframe.H4);
    }

    [Theory]
    [InlineData("2026-03-17T10:03:12Z", 1, "2026-03-17T10:03:00Z")]
    [InlineData("2026-03-17T10:03:12Z", 5, "2026-03-17T10:00:00Z")]
    [InlineData("2026-03-17T10:14:59Z", 15, "2026-03-17T10:00:00Z")]
    [InlineData("2026-03-17T10:15:00Z", 15, "2026-03-17T10:15:00Z")]
    [InlineData("2026-03-17T10:00:00Z", 5, "2026-03-17T10:00:00Z")]
    [InlineData("2026-03-17T23:59:59Z", 1, "2026-03-17T23:59:00Z")]
    [InlineData("2026-03-17T10:04:59Z", 5, "2026-03-17T10:00:00Z")]
    [InlineData("2026-03-17T10:05:00Z", 5, "2026-03-17T10:05:00Z")]
    [InlineData("2026-03-17T10:29:59Z", 15, "2026-03-17T10:15:00Z")]
    [InlineData("2026-03-17T10:30:00Z", 15, "2026-03-17T10:30:00Z")]
    public void Floor_AssignsCorrectBucket(string input, int tfMinutes, string expected)
    {
        var tf = Timeframe.All.First(t => t.Minutes == tfMinutes);
        var ts = DateTimeOffset.Parse(input);
        var expectedTs = DateTimeOffset.Parse(expected);

        tf.Floor(ts).Should().Be(expectedTs);
    }

    [Fact]
    public void Floor_AtExactBoundary_ReturnsSame()
    {
        var ts = new DateTimeOffset(2026, 3, 17, 10, 15, 0, TimeSpan.Zero);
        Timeframe.M15.Floor(ts).Should().Be(ts);
        Timeframe.M5.Floor(ts).Should().Be(ts);
        Timeframe.M1.Floor(ts).Should().Be(ts);
    }
}
