using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Engine;
using FluentAssertions;

namespace EthSignal.Tests.Engine.Indicators;

/// <summary>P2-T5: Closed-candle policy — indicator engine must not mark incomplete-candle values as final.</summary>
public class ClosedCandlePolicyTests
{
    private static RichCandle MakeCandle(DateTimeOffset time, decimal price, bool isClosed = true) => new()
    {
        OpenTime = time,
        BidOpen = price, BidHigh = price + 5, BidLow = price - 5, BidClose = price + 2,
        AskOpen = price + 1, AskHigh = price + 6, AskLow = price - 4, AskClose = price + 3,
        Volume = 100m, IsClosed = isClosed
    };

    [Fact]
    public void OpenCandle_Produces_Provisional_Snapshot()
    {
        var t0 = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero);
        var candles = Enumerable.Range(0, 60)
            .Select(i => MakeCandle(t0.AddMinutes(i * 5), 2000m + i, isClosed: i < 59))
            .ToList();

        // Last candle is still open (not closed)
        candles[59].IsClosed.Should().BeFalse();

        var snapshots = IndicatorEngine.ComputeAll("ETHUSD", "5m", candles);

        snapshots.Should().HaveCount(60);

        // The last snapshot (open candle) must be provisional
        snapshots[59].IsProvisional.Should().BeTrue("open candle indicator should be provisional");

        // A warm-up-period candle that IS closed should still be provisional due to warm-up
        snapshots[0].IsProvisional.Should().BeTrue("warm-up period candle should be provisional");

        // A candle past warm-up that IS closed should NOT be provisional
        snapshots[50].IsProvisional.Should().BeFalse("closed candle past warm-up should be final");
    }

    [Fact]
    public void AllClosed_Past_WarmUp_Not_Provisional()
    {
        var t0 = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero);
        var candles = Enumerable.Range(0, 60)
            .Select(i => MakeCandle(t0.AddMinutes(i * 5), 2000m + i, isClosed: true))
            .ToList();

        var snapshots = IndicatorEngine.ComputeAll("ETHUSD", "5m", candles);

        // Past warm-up (index >= 49), all closed candles should not be provisional
        for (int i = IndicatorEngine.WarmUpPeriod - 1; i < 60; i++)
            snapshots[i].IsProvisional.Should().BeFalse($"candle at index {i} is closed and past warm-up");
    }

    [Fact]
    public void ComputeLatest_On_Open_Candle_Returns_Provisional()
    {
        var t0 = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero);
        var candles = Enumerable.Range(0, 55)
            .Select(i => MakeCandle(t0.AddMinutes(i * 5), 2000m + i, isClosed: i < 54))
            .ToList();

        var latest = IndicatorEngine.ComputeLatest("ETHUSD", "5m", candles);

        latest.Should().NotBeNull();
        latest!.IsProvisional.Should().BeTrue("latest is an open candle");
    }

    [Fact]
    public void ComputeLatest_On_Closed_Candle_Past_WarmUp_Not_Provisional()
    {
        var t0 = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero);
        var candles = Enumerable.Range(0, 55)
            .Select(i => MakeCandle(t0.AddMinutes(i * 5), 2000m + i, isClosed: true))
            .ToList();

        var latest = IndicatorEngine.ComputeLatest("ETHUSD", "5m", candles);

        latest.Should().NotBeNull();
        latest!.IsProvisional.Should().BeFalse("latest is closed and past warm-up");
    }
}
