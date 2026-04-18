using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Engine;
using FluentAssertions;

namespace EthSignal.Tests.Engine;

/// <summary>Phase-gate tests verifying cross-phase integration correctness.</summary>
public class PhaseGateTests
{
    // ─── P5-01: Session limit enforcement ─────────────────

    [Fact]
    public void SessionLimits_MaxOpenPositions_Blocks()
    {
        var policy = new RiskPolicy { MaxOpenPositions = 1 };
        var result = RiskManager.CheckSessionLimits(policy, openPositionCount: 1, []);
        result.Should().NotBeNull();
        result.Should().Contain("MaxOpenPositions");
    }

    [Fact]
    public void SessionLimits_NoOpenPositions_Allows()
    {
        var policy = new RiskPolicy { MaxOpenPositions = 1 };
        var result = RiskManager.CheckSessionLimits(policy, openPositionCount: 0, []);
        result.Should().BeNull();
    }

    [Fact]
    public void SessionLimits_ConsecutiveLosses_Blocks()
    {
        var policy = new RiskPolicy { MaxConsecutiveLossesPerDay = 2, RiskPercentPerTrade = 0.5m, DailyMaxDrawdownPercent = decimal.MaxValue };
        var outcomes = new List<SignalOutcome>
        {
            new() { SignalId = Guid.NewGuid(), OutcomeLabel = OutcomeLabel.WIN, PnlR = 1.5m },
            new() { SignalId = Guid.NewGuid(), OutcomeLabel = OutcomeLabel.LOSS, PnlR = -1m },
            new() { SignalId = Guid.NewGuid(), OutcomeLabel = OutcomeLabel.LOSS, PnlR = -1m },
        };

        var result = RiskManager.CheckSessionLimits(policy, openPositionCount: 0, outcomes);
        result.Should().NotBeNull();
        result.Should().Contain("ConsecutiveLosses");
    }

    [Fact]
    public void SessionLimits_ConsecutiveLosses_ResetByWin()
    {
        var policy = new RiskPolicy { MaxConsecutiveLossesPerDay = 2, RiskPercentPerTrade = 0.5m, DailyMaxDrawdownPercent = decimal.MaxValue };
        var outcomes = new List<SignalOutcome>
        {
            new() { SignalId = Guid.NewGuid(), OutcomeLabel = OutcomeLabel.LOSS, PnlR = -1m },
            new() { SignalId = Guid.NewGuid(), OutcomeLabel = OutcomeLabel.LOSS, PnlR = -1m },
            new() { SignalId = Guid.NewGuid(), OutcomeLabel = OutcomeLabel.WIN, PnlR = 1.5m },
            new() { SignalId = Guid.NewGuid(), OutcomeLabel = OutcomeLabel.LOSS, PnlR = -1m },
        };

        var result = RiskManager.CheckSessionLimits(policy, openPositionCount: 0, outcomes);
        result.Should().BeNull("win broke the consecutive loss streak");
    }

    [Fact]
    public void SessionLimits_DailyDrawdown_Blocks()
    {
        var policy = new RiskPolicy
        {
            DailyMaxDrawdownPercent = 1.5m, // Threshold 1.5% — 4 losses × 0.5% = 2.0% exceeds it
            RiskPercentPerTrade = 0.5m,
            MaxConsecutiveLossesPerDay = 10,
            MaxOpenPositions = 5
        };
        // 4 losses of -1R each → daily drawdown = 4 * 0.5% = 2.0% (meets threshold)
        var outcomes = new List<SignalOutcome>
        {
            new() { SignalId = Guid.NewGuid(), OutcomeLabel = OutcomeLabel.LOSS, PnlR = -1m },
            new() { SignalId = Guid.NewGuid(), OutcomeLabel = OutcomeLabel.LOSS, PnlR = -1m },
            new() { SignalId = Guid.NewGuid(), OutcomeLabel = OutcomeLabel.LOSS, PnlR = -1m },
            new() { SignalId = Guid.NewGuid(), OutcomeLabel = OutcomeLabel.LOSS, PnlR = -1m },
        };

        var result = RiskManager.CheckSessionLimits(policy, openPositionCount: 0, outcomes);
        result.Should().NotBeNull();
        result.Should().Contain("DailyDrawdown");
    }

    /// <summary>B-08: Wins must NOT offset drawdown — losses-only calculation.</summary>
    [Fact]
    public void SessionLimits_DailyDrawdown_Ignores_Wins()
    {
        var policy = new RiskPolicy
        {
            DailyMaxDrawdownPercent = 1.5m, // Threshold 1.5% — 4 losses × 0.5% = 2.0% exceeds it
            RiskPercentPerTrade = 0.5m,
            MaxConsecutiveLossesPerDay = 10,
            MaxOpenPositions = 5
        };
        // 4 losses (-1R each) + 3 wins (+1.5R each) → net PnL = +0.5R (positive!)
        // But realized losses = 4 * 0.5% = 2.0% → should STILL block
        var outcomes = new List<SignalOutcome>
        {
            new() { SignalId = Guid.NewGuid(), OutcomeLabel = OutcomeLabel.WIN, PnlR = 1.5m },
            new() { SignalId = Guid.NewGuid(), OutcomeLabel = OutcomeLabel.LOSS, PnlR = -1m },
            new() { SignalId = Guid.NewGuid(), OutcomeLabel = OutcomeLabel.WIN, PnlR = 1.5m },
            new() { SignalId = Guid.NewGuid(), OutcomeLabel = OutcomeLabel.LOSS, PnlR = -1m },
            new() { SignalId = Guid.NewGuid(), OutcomeLabel = OutcomeLabel.LOSS, PnlR = -1m },
            new() { SignalId = Guid.NewGuid(), OutcomeLabel = OutcomeLabel.WIN, PnlR = 1.5m },
            new() { SignalId = Guid.NewGuid(), OutcomeLabel = OutcomeLabel.LOSS, PnlR = -1m },
        };

        var result = RiskManager.CheckSessionLimits(policy, openPositionCount: 0, outcomes);
        result.Should().NotBeNull("wins should not offset realized losses for drawdown");
        result.Should().Contain("DailyDrawdown");
    }

    // ─── P6-03: Outcome timestamps use candle time ────────

    [Fact]
    public void Outcome_ClosedAt_Uses_Candle_Time_Not_Now()
    {
        var signalTime = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero);
        var signal = new SignalRecommendation
        {
            Symbol = "ETHUSD",
            Timeframe = "5m",
            SignalTimeUtc = signalTime,
            Direction = SignalDirection.BUY,
            EntryPrice = 2000m,
            TpPrice = 2015m,
            SlPrice = 1990m,
            Regime = Regime.BULLISH,
            Reasons = ["test"]
        };

        // Future candles starting at 10:05
        var candles = new List<RichCandle>
        {
            new()
            {
                OpenTime = signalTime.AddMinutes(5), // 10:05
                BidOpen = 2001, BidHigh = 2020, BidLow = 2000, BidClose = 2016,
                AskOpen = 2002, AskHigh = 2021, AskLow = 2001, AskClose = 2017,
                Volume = 100, IsClosed = true
            }
        };

        var outcome = OutcomeEvaluator.Evaluate(signal, candles);
        outcome.OutcomeLabel.Should().Be(OutcomeLabel.WIN);
        // ClosedAtUtc should be candle close time (10:05 + 5m = 10:10), NOT DateTimeOffset.UtcNow
        outcome.ClosedAtUtc.Should().NotBeNull();
        outcome.ClosedAtUtc!.Value.Should().Be(signalTime.AddMinutes(10),
            "ClosedAtUtc should be the resolving candle's close time (open + 5m)");
    }

    [Fact]
    public void Outcome_Expired_ClosedAt_Uses_Last_Candle_Time()
    {
        var signalTime = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero);
        var signal = new SignalRecommendation
        {
            Symbol = "ETHUSD",
            Timeframe = "5m",
            SignalTimeUtc = signalTime,
            Direction = SignalDirection.BUY,
            EntryPrice = 2000m,
            TpPrice = 2100m,
            SlPrice = 1900m, // Wide TP/SL — won't be hit
            Regime = Regime.BULLISH,
            Reasons = ["test"]
        };

        const int testTimeoutBars = 60; // Explicit — test is independent of StrategyParameters.Default
        var p = StrategyParameters.Default with { OutcomeTimeoutBars = testTimeoutBars, IntradayTimeoutBars = testTimeoutBars };

        // Generate 60 candles that don't hit TP or SL
        var candles = Enumerable.Range(0, testTimeoutBars).Select(i => new RichCandle
        {
            OpenTime = signalTime.AddMinutes(5 + i * 5),
            BidOpen = 2000,
            BidHigh = 2005,
            BidLow = 1995,
            BidClose = 2001,
            AskOpen = 2001,
            AskHigh = 2006,
            AskLow = 1996,
            AskClose = 2002,
            Volume = 100,
            IsClosed = true
        }).ToList();

        var outcome = OutcomeEvaluator.Evaluate(signal, candles, p, Timeframe.M5);
        outcome.OutcomeLabel.Should().Be(OutcomeLabel.EXPIRED);
        outcome.ClosedAtUtc.Should().NotBeNull();
        var lastCandleCloseTime = candles[^1].OpenTime.AddMinutes(5);
        outcome.ClosedAtUtc!.Value.Should().Be(lastCandleCloseTime);
    }

    // ─── P4-01: Signal time = candle close (not open) ─────

    [Fact]
    public void Signal_Time_Is_Candle_Close_Not_Open()
    {
        var candleOpenTime = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero);
        var regime = new RegimeResult
        {
            Symbol = "ETHUSD",
            CandleOpenTimeUtc = candleOpenTime,
            Regime = Regime.BULLISH,
            RegimeScore = 6,
            TriggeredConditions = ["all"],
            DisqualifyingConditions = []
        };

        var snap = new IndicatorSnapshot
        {
            Symbol = "ETHUSD",
            Timeframe = "5m",
            CandleOpenTimeUtc = candleOpenTime,
            Ema20 = 2090,
            Ema50 = 2085,
            Rsi14 = 52,
            Macd = 0.5m,
            MacdSignal = 0.3m,
            MacdHist = 0.5m,
            Atr14 = 10,
            Adx14 = 22,
            PlusDi = 25,
            MinusDi = 15,
            VolumeSma20 = 100,
            Vwap = 2080,
            Spread = 1m,
            CloseMid = 2100,
            IsProvisional = false
        };
        var prevSnap = snap with { Rsi14 = 45, MacdHist = 0.2m, CloseMid = 2085, Ema20 = 2088 };
        var candle = new RichCandle
        {
            OpenTime = candleOpenTime,
            BidOpen = 2087,
            BidHigh = 2105,
            BidLow = 2085,
            BidClose = 2100,
            AskOpen = 2088,
            AskHigh = 2106,
            AskLow = 2086,
            AskClose = 2101,
            Volume = 150,
            IsClosed = true
        };

        var result = SignalEngine.Evaluate("ETHUSD", regime, snap, prevSnap, candle);

        // Signal time should be candle open + 5 minutes (candle close time)
        result.SignalTimeUtc.Should().Be(candleOpenTime.AddMinutes(5),
            "P4-01: SignalTimeUtc = candle close time (open + 5m)");
    }

    // ─── P3-01: Regime scored model ───────────────────────

    [Fact]
    public void Regime_Scored_Model_Distinguishes_Mandatory_From_Scored()
    {
        var t0 = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero);
        // All mandatory pass (EMA alignment, slope, ADX), but scored conditions fail
        // Close < VWAP (fail), -DI > +DI (fail), no market structure
        var snapshots = new List<IndicatorSnapshot>();
        for (int i = 0; i < 5; i++)
        {
            snapshots.Add(new IndicatorSnapshot
            {
                Symbol = "ETHUSD",
                Timeframe = "15m",
                CandleOpenTimeUtc = t0.AddMinutes(15 * i),
                Ema20 = 100 + i,
                Ema50 = 95,
                Rsi14 = 55,
                Macd = 0,
                MacdSignal = 0,
                MacdHist = 0,
                Atr14 = 10,
                Adx14 = 20,
                PlusDi = 12,
                MinusDi = 30, // -DI > +DI (bearish DI)
                VolumeSma20 = 100,
                Vwap = 200, // Close far below VWAP
                Spread = 1,
                CloseMid = 90, // Close < VWAP
                IsProvisional = false
            });
        }

        var result = RegimeAnalyzer.Classify("ETHUSD", snapshots);
        // All 3 mandatory conditions pass → should be BULLISH
        result.Regime.Should().Be(Regime.BULLISH,
            "mandatory conditions pass even though scored conditions fail");
        // Score should be >= 3 (mandatory only) but < 6 (not all scored pass)
        result.RegimeScore.Should().BeGreaterThanOrEqualTo(3);
    }
}
