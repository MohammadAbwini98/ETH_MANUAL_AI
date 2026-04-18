using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Engine.ML;
using FluentAssertions;

namespace EthSignal.Tests.Engine.ML;

public class MlRecentSnapshotBufferTests
{
    [Fact]
    public void Add_Keeps_Timeframes_Isolated()
    {
        var buffer = new MlRecentSnapshotBuffer();

        buffer.Add("5m", MakeSnapshot("5m", 10));
        buffer.Add("15m", MakeSnapshot("15m", 20));
        buffer.Add("5m", MakeSnapshot("5m", 11));

        buffer.Get("5m").Should().HaveCount(2);
        buffer.Get("5m").Select(s => s.Timeframe).Should().OnlyContain(tf => tf == "5m");
        buffer.Get("15m").Should().ContainSingle();
        buffer.Get("15m")[0].Timeframe.Should().Be("15m");
        buffer.Get("1h").Should().BeEmpty();
    }

    [Fact]
    public void Add_Trims_Per_Timeframe_Independently()
    {
        var buffer = new MlRecentSnapshotBuffer(maxPerTimeframe: 3);

        for (var i = 0; i < 5; i++)
            buffer.Add("5m", MakeSnapshot("5m", i));

        buffer.Add("15m", MakeSnapshot("15m", 99));

        buffer.Get("5m").Should().HaveCount(3);
        buffer.Get("5m").Select(s => s.CandleOpenTimeUtc.Minute).Should().Equal(4, 3, 2);
        buffer.Get("15m").Should().ContainSingle();
    }

    private static IndicatorSnapshot MakeSnapshot(string timeframe, int minuteOffset)
    {
        var candleTime = new DateTimeOffset(2026, 4, 10, 10, 0, 0, TimeSpan.Zero).AddMinutes(minuteOffset);

        return new IndicatorSnapshot
        {
            Symbol = "ETHUSD",
            Timeframe = timeframe,
            CandleOpenTimeUtc = candleTime,
            CloseMid = 2200m + minuteOffset,
            Ema20 = 2200m,
            Ema50 = 2195m,
            Rsi14 = 55m,
            Macd = 1m,
            MacdSignal = 0.8m,
            MacdHist = 0.2m,
            Atr14 = 4m,
            Adx14 = 25m,
            PlusDi = 20m,
            MinusDi = 15m,
            VolumeSma20 = 100m,
            Vwap = 2199m,
            Spread = 0.5m,
            MidHigh = 2202m,
            MidLow = 2198m,
            IsProvisional = false
        };
    }
}
