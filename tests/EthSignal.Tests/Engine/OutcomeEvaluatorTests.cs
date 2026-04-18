using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Engine;
using FluentAssertions;

namespace EthSignal.Tests.Engine;

/// <summary>P6 tests: Signal outcome tracking and analytics.</summary>
public class OutcomeEvaluatorTests
{
    private static RichCandle MakeCandle(DateTimeOffset time, decimal midH, decimal midL, decimal midC) => new()
    {
        OpenTime = time,
        BidOpen = midC - 1, BidHigh = midH - 0.5m, BidLow = midL - 0.5m, BidClose = midC - 0.5m,
        AskOpen = midC + 1, AskHigh = midH + 0.5m, AskLow = midL + 0.5m, AskClose = midC + 0.5m,
        Volume = 100, IsClosed = true
    };

    private static SignalRecommendation MakeBuySignal(decimal entry, decimal tp, decimal sl) => new()
    {
        Symbol = "ETHUSD", Timeframe = "5m",
        SignalTimeUtc = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero),
        Direction = SignalDirection.BUY,
        EntryPrice = entry, TpPrice = tp, SlPrice = sl,
        Regime = Regime.BULLISH, Reasons = ["test"]
    };

    private static SignalRecommendation MakeSellSignal(decimal entry, decimal tp, decimal sl) => new()
    {
        Symbol = "ETHUSD", Timeframe = "5m",
        SignalTimeUtc = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero),
        Direction = SignalDirection.SELL,
        EntryPrice = entry, TpPrice = tp, SlPrice = sl,
        Regime = Regime.BEARISH, Reasons = ["test"]
    };

    /// <summary>P6-T2: WIN outcome — TP hit first.</summary>
    [Fact]
    public void BUY_TP_Hit_First_Is_WIN()
    {
        var signal = MakeBuySignal(entry: 2000, tp: 2015, sl: 1990);
        var t = signal.SignalTimeUtc;
        var candles = new List<RichCandle>
        {
            MakeCandle(t.AddMinutes(5), midH: 2008, midL: 1995, midC: 2005),
            MakeCandle(t.AddMinutes(10), midH: 2016, midL: 2003, midC: 2014), // TP hit
        };

        var outcome = OutcomeEvaluator.Evaluate(signal, candles);

        outcome.OutcomeLabel.Should().Be(OutcomeLabel.WIN);
        outcome.TpHit.Should().BeTrue();
        outcome.PnlR.Should().Be(1.5m); // (2015-2000)/(2000-1990) = 15/10 = 1.5
        outcome.BarsObserved.Should().Be(2);
    }

    /// <summary>P6-T3: LOSS outcome — SL hit first.</summary>
    [Fact]
    public void BUY_SL_Hit_First_Is_LOSS()
    {
        var signal = MakeBuySignal(entry: 2000, tp: 2015, sl: 1990);
        var t = signal.SignalTimeUtc;
        var candles = new List<RichCandle>
        {
            MakeCandle(t.AddMinutes(5), midH: 2005, midL: 1989, midC: 1992), // SL hit
        };

        var outcome = OutcomeEvaluator.Evaluate(signal, candles);

        outcome.OutcomeLabel.Should().Be(OutcomeLabel.LOSS);
        outcome.SlHit.Should().BeTrue();
        outcome.PnlR.Should().Be(-1m); // (1990-2000)/10 = -1
    }

    [Fact]
    public void SELL_TP_Hit_Is_WIN()
    {
        var signal = MakeSellSignal(entry: 2000, tp: 1985, sl: 2010);
        var t = signal.SignalTimeUtc;
        var candles = new List<RichCandle>
        {
            MakeCandle(t.AddMinutes(5), midH: 2005, midL: 1984, midC: 1988), // TP hit
        };

        var outcome = OutcomeEvaluator.Evaluate(signal, candles);

        outcome.OutcomeLabel.Should().Be(OutcomeLabel.WIN);
        outcome.PnlR.Should().Be(1.5m);
    }

    [Fact]
    public void SELL_SL_Hit_Is_LOSS()
    {
        var signal = MakeSellSignal(entry: 2000, tp: 1985, sl: 2010);
        var t = signal.SignalTimeUtc;
        var candles = new List<RichCandle>
        {
            MakeCandle(t.AddMinutes(5), midH: 2011, midL: 1995, midC: 2008), // SL hit
        };

        var outcome = OutcomeEvaluator.Evaluate(signal, candles);

        outcome.OutcomeLabel.Should().Be(OutcomeLabel.LOSS);
        outcome.PnlR.Should().Be(-1m);
    }

    [Fact]
    public void BUY_Ambiguous_Parent_Candle_Uses_1m_SubBars_To_Resolve_First_Touch()
    {
        var signal = MakeBuySignal(entry: 2000, tp: 2010, sl: 1990);
        var t = signal.SignalTimeUtc;
        var candles = new List<RichCandle>
        {
            MakeCandle(t.AddMinutes(5), midH: 2012, midL: 1988, midC: 2001),
        };
        var subBars = new List<RichCandle>
        {
            MakeCandle(t.AddMinutes(5), midH: 2011, midL: 1998, midC: 2009), // TP first
            MakeCandle(t.AddMinutes(6), midH: 2005, midL: 1988, midC: 1992),
        };

        var outcome = OutcomeEvaluator.Evaluate(signal, candles, StrategyParameters.Default, Timeframe.M5, subBars, Timeframe.M1);

        outcome.OutcomeLabel.Should().Be(OutcomeLabel.WIN);
        outcome.PnlR.Should().Be(1m);
    }

    [Fact]
    public void Ambiguous_Without_SubBars_Remains_Ambiguous_And_Does_Not_Count_As_Loss()
    {
        var signal = MakeBuySignal(entry: 2000, tp: 2010, sl: 1990);
        var t = signal.SignalTimeUtc;
        var candles = new List<RichCandle>
        {
            MakeCandle(t.AddMinutes(5), midH: 2012, midL: 1988, midC: 2001),
        };

        var outcome = OutcomeEvaluator.Evaluate(signal, candles);

        outcome.OutcomeLabel.Should().Be(OutcomeLabel.AMBIGUOUS);
        outcome.PnlR.Should().Be(0);
    }

    /// <summary>B-02: Fewer than TimeoutBars candles with no TP/SL → PENDING, not EXPIRED.</summary>
    [Fact]
    public void Insufficient_Candles_Returns_PENDING()
    {
        var signal = MakeBuySignal(entry: 2000, tp: 2015, sl: 1990);
        var t = signal.SignalTimeUtc;
        // Price never hits TP or SL in 3 candles (< TimeoutBars)
        var candles = new List<RichCandle>
        {
            MakeCandle(t.AddMinutes(5), midH: 2005, midL: 1995, midC: 2002),
            MakeCandle(t.AddMinutes(10), midH: 2006, midL: 1994, midC: 2004),
            MakeCandle(t.AddMinutes(15), midH: 2007, midL: 1993, midC: 2003),
        };

        var outcome = OutcomeEvaluator.Evaluate(signal, candles);

        outcome.OutcomeLabel.Should().Be(OutcomeLabel.PENDING);
        outcome.BarsObserved.Should().Be(3);
    }

    /// <summary>B-02: Exactly TimeoutBars candles with no TP/SL → EXPIRED.</summary>
    [Fact]
    public void Full_Timeout_Returns_EXPIRED()
    {
        const int testTimeoutBars = 60; // Explicit — test is independent of StrategyParameters.Default
        var p = StrategyParameters.Default with { OutcomeTimeoutBars = testTimeoutBars, IntradayTimeoutBars = testTimeoutBars };
        var signal = MakeBuySignal(entry: 2000, tp: 2100, sl: 1900); // Wide TP/SL — won't be hit
        var t = signal.SignalTimeUtc;
        var candles = Enumerable.Range(0, testTimeoutBars).Select(i =>
            MakeCandle(t.AddMinutes(5 + i * 5), midH: 2005, midL: 1995, midC: 2001)
        ).ToList();

        var outcome = OutcomeEvaluator.Evaluate(signal, candles, p, Timeframe.M5);

        outcome.OutcomeLabel.Should().Be(OutcomeLabel.EXPIRED);
        outcome.BarsObserved.Should().Be(testTimeoutBars);
        outcome.ClosedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public void Zero_Candles_Returns_PENDING()
    {
        var signal = MakeBuySignal(entry: 2000, tp: 2015, sl: 1990);
        var outcome = OutcomeEvaluator.Evaluate(signal, []);

        outcome.OutcomeLabel.Should().Be(OutcomeLabel.PENDING);
        outcome.BarsObserved.Should().Be(0);
    }

    /// <summary>P6-T4: Analytics — WinRate, AverageR, ProfitFactor.</summary>
    [Fact]
    public void ComputeStats_Correct()
    {
        var outcomes = new List<SignalOutcome>
        {
            new() { SignalId = Guid.NewGuid(), OutcomeLabel = OutcomeLabel.WIN, PnlR = 1.5m },
            new() { SignalId = Guid.NewGuid(), OutcomeLabel = OutcomeLabel.WIN, PnlR = 1.5m },
            new() { SignalId = Guid.NewGuid(), OutcomeLabel = OutcomeLabel.LOSS, PnlR = -1.0m },
            new() { SignalId = Guid.NewGuid(), OutcomeLabel = OutcomeLabel.EXPIRED, PnlR = 0.2m },
            new() { SignalId = Guid.NewGuid(), OutcomeLabel = OutcomeLabel.AMBIGUOUS, PnlR = 0m },
        };

        var stats = OutcomeEvaluator.ComputeStats(outcomes);

        stats.TotalSignals.Should().Be(5);
        stats.ResolvedSignals.Should().Be(5);
        stats.Wins.Should().Be(2);
        stats.Losses.Should().Be(1);
        stats.Expired.Should().Be(1);
        stats.Ambiguous.Should().Be(1);
        // T3-13: WinRate = wins / (wins + losses) = 2 / (2+1) = 66.67% (EXPIRED excluded from denominator)
        stats.WinRate.Should().BeApproximately(66.67m, 0.01m);
        // AverageR = (1.5 + 1.5 - 1.0 + 0.2) / 4 = 0.55
        stats.AverageR.Should().Be(0.55m);
        // ProfitFactor = (1.5+1.5+0.2) / 1.0 = 3.2
        stats.ProfitFactor.Should().Be(3.2m);
    }
}
