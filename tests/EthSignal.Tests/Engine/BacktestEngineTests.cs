using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Engine;
using FluentAssertions;

namespace EthSignal.Tests.Engine;

/// <summary>P9 tests: Backtesting, look-ahead prevention, reproducibility.</summary>
public class BacktestEngineTests
{
    private static RichCandle MakeCandle(DateTimeOffset time, decimal basePrice, bool closed = true) => new()
    {
        OpenTime = time,
        BidOpen = basePrice, BidHigh = basePrice + 5, BidLow = basePrice - 5, BidClose = basePrice + 2,
        AskOpen = basePrice + 1, AskHigh = basePrice + 6, AskLow = basePrice - 4, AskClose = basePrice + 3,
        Volume = 100m, IsClosed = closed
    };

    private static List<RichCandle> GenerateCandles(Timeframe tf, int count, decimal startPrice)
    {
        var t0 = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        return Enumerable.Range(0, count)
            .Select(i => MakeCandle(t0.AddMinutes(i * tf.Minutes), startPrice + i * 0.1m))
            .ToList();
    }

    /// <summary>P9-T1: Look-ahead prevention — backtest at candle t only uses data up to t.</summary>
    [Fact]
    public void Backtest_Does_Not_Use_Future_Data()
    {
        // Generate enough candles for warm-up + trading + evaluation
        var candles5m = GenerateCandles(Timeframe.M5, 200, 2000m);
        var candles15m = GenerateCandles(Timeframe.M15, 200, 2000m);

        var result = BacktestEngine.Run("ETHUSD", candles5m, candles15m, new RiskPolicy());

        // The engine should process without error
        // Key verification: no signal should reference a candle time that is in the future
        // relative to the signal time
        foreach (var sig in result.Signals)
        {
            sig.SignalTimeUtc.Should().BeBefore(candles5m[^1].OpenTime,
                "signal should be generated from historical data, not future");
        }
    }

    /// <summary>P9-T2: Reproducibility — same inputs produce same outputs.</summary>
    [Fact]
    public void Same_Input_Produces_Same_Output()
    {
        var candles5m = GenerateCandles(Timeframe.M5, 150, 2000m);
        var candles15m = GenerateCandles(Timeframe.M15, 150, 2000m);
        var policy = new RiskPolicy();

        var result1 = BacktestEngine.Run("ETHUSD", candles5m, candles15m, policy);
        var result2 = BacktestEngine.Run("ETHUSD", candles5m, candles15m, policy);

        result1.Signals.Count.Should().Be(result2.Signals.Count);
        result1.Stats.Wins.Should().Be(result2.Stats.Wins);
        result1.Stats.Losses.Should().Be(result2.Stats.Losses);
        result1.Stats.TotalPnlR.Should().Be(result2.Stats.TotalPnlR);
    }

    /// <summary>P9-T3: Stability — runs without crash on full dataset.</summary>
    [Fact]
    public void Backtest_Completes_Without_Error()
    {
        var candles5m = GenerateCandles(Timeframe.M5, 300, 2000m);
        var candles15m = GenerateCandles(Timeframe.M15, 100, 2000m);

        var act = () => BacktestEngine.Run("ETHUSD", candles5m, candles15m, new RiskPolicy());

        act.Should().NotThrow();
    }

    [Fact]
    public void Insufficient_Data_Returns_Empty()
    {
        var candles5m = GenerateCandles(Timeframe.M5, 20, 2000m);
        var candles15m = GenerateCandles(Timeframe.M15, 20, 2000m);

        var result = BacktestEngine.Run("ETHUSD", candles5m, candles15m, new RiskPolicy());

        result.Signals.Should().BeEmpty();
        result.Stats.TotalSignals.Should().Be(0);
    }
}
