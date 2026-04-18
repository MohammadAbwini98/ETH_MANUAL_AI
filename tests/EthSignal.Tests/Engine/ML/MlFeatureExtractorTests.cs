using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Engine;
using EthSignal.Infrastructure.Engine.ML;
using FluentAssertions;

namespace EthSignal.Tests.Engine.ML;

public class MlFeatureExtractorTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 3, 17, 14, 0, 0, TimeSpan.Zero);

    private static IndicatorSnapshot MakeSnap(
        decimal closeMid = 2100m, decimal ema20 = 2090m, decimal ema50 = 2080m,
        decimal rsi = 55m, decimal macdHist = 0.5m, decimal adx = 25m,
        decimal plusDi = 25m, decimal minusDi = 15m, decimal atr = 10m,
        decimal vwap = 2080m, decimal volSma = 100m, decimal spread = 1m,
        int hourOffset = 0) => new()
        {
            Symbol = "ETHUSD",
            Timeframe = "5m",
            CandleOpenTimeUtc = BaseTime.AddHours(hourOffset),
            Ema20 = ema20,
            Ema50 = ema50,
            Rsi14 = rsi,
            Macd = 0.5m,
            MacdSignal = 0.3m,
            MacdHist = macdHist,
            Atr14 = atr,
            Adx14 = adx,
            PlusDi = plusDi,
            MinusDi = minusDi,
            VolumeSma20 = volSma,
            Vwap = vwap,
            Spread = spread,
            CloseMid = closeMid,
            MidHigh = closeMid + 5m,
            MidLow = closeMid - 5m,
            IsProvisional = false
        };

    private static RichCandle MakeCandle(decimal close = 2100m, decimal vol = 150m) => new()
    {
        OpenTime = BaseTime,
        BidOpen = close - 3,
        BidHigh = close + 5,
        BidLow = close - 5,
        BidClose = close,
        AskOpen = close - 2,
        AskHigh = close + 6,
        AskLow = close - 4,
        AskClose = close + 1,
        Volume = vol,
        IsClosed = true
    };

    private static RegimeResult MakeRegime(Regime regime = Regime.BULLISH, int score = 5) => new()
    {
        Symbol = "ETHUSD",
        CandleOpenTimeUtc = BaseTime,
        Regime = regime,
        RegimeScore = score,
        TriggeredConditions = ["all"],
        DisqualifyingConditions = []
    };

    private static SignalOutcome MakeOutcome(OutcomeLabel label, decimal pnlR = 0.5m) => new()
    {
        SignalId = Guid.NewGuid(),
        OutcomeLabel = label,
        PnlR = pnlR
    };

    // ─── Basic extraction produces 80 features ────────────

    [Fact]
    public void Extract_Produces_80_Features_In_FloatArray()
    {
        var snap = MakeSnap();
        var prevSnap = MakeSnap(rsi: 50, macdHist: 0.3m);
        var recentSnaps = Enumerable.Range(0, 20)
            .Select(i => MakeSnap(hourOffset: -i))
            .ToList();
        var candle = MakeCandle();
        var regime = MakeRegime();
        var outcomes = new List<SignalOutcome>();

        var fv = MlFeatureExtractor.Extract(
            snap, prevSnap, recentSnaps, candle, regime,
            SignalDirection.BUY, 75, outcomes, 10);

        fv.ToFloatArray().Should().HaveCount(80);
    }

    [Fact]
    public void Feature_Names_Count_Matches_FloatArray()
    {
        MlFeatureVector.FeatureNames.Count.Should().Be(80);
    }

    // ─── Category A: Raw indicator passthrough ────────────

    [Fact]
    public void Extract_CategoryA_PassesThrough_Raw_Indicators()
    {
        var snap = MakeSnap(closeMid: 2100, ema20: 2090, rsi: 55, adx: 25);
        var candle = MakeCandle(vol: 200);

        var fv = MlFeatureExtractor.Extract(
            snap, null, new List<IndicatorSnapshot>(), candle,
            MakeRegime(), SignalDirection.BUY, 70, new List<SignalOutcome>(), 5);

        fv.Ema20.Should().Be(2090m);
        fv.Rsi14.Should().Be(55m);
        fv.Adx14.Should().Be(25m);
        fv.CloseMid.Should().Be(2100m);
        fv.Volume.Should().Be(200m);
    }

    // ─── Category B: Derived features ────────────────────

    [Fact]
    public void Extract_Ema20MinusEma50_Computed()
    {
        var snap = MakeSnap(ema20: 2100m, ema50: 2080m);

        var fv = MlFeatureExtractor.Extract(
            snap, null, new List<IndicatorSnapshot>(), MakeCandle(),
            MakeRegime(), SignalDirection.BUY, 70, new List<SignalOutcome>(), 5);

        fv.Ema20MinusEma50.Should().Be(20m);
    }

    [Fact]
    public void Extract_Rsi14Delta_Uses_PrevSnap()
    {
        var snap = MakeSnap(rsi: 60);
        var prev = MakeSnap(rsi: 50);

        var fv = MlFeatureExtractor.Extract(
            snap, prev, new List<IndicatorSnapshot>(), MakeCandle(),
            MakeRegime(), SignalDirection.BUY, 70, new List<SignalOutcome>(), 5);

        fv.Rsi14Delta.Should().Be(10m);
    }

    [Fact]
    public void Extract_Rsi14Delta_Zero_When_NoPrevSnap()
    {
        var snap = MakeSnap(rsi: 60);

        var fv = MlFeatureExtractor.Extract(
            snap, null, new List<IndicatorSnapshot>(), MakeCandle(),
            MakeRegime(), SignalDirection.BUY, 70, new List<SignalOutcome>(), 5);

        fv.Rsi14Delta.Should().Be(0);
    }

    [Fact]
    public void Extract_MacdHistDelta_Computed()
    {
        var snap = MakeSnap(macdHist: 0.8m);
        var prev = MakeSnap(macdHist: 0.3m);

        var fv = MlFeatureExtractor.Extract(
            snap, prev, new List<IndicatorSnapshot>(), MakeCandle(),
            MakeRegime(), SignalDirection.BUY, 70, new List<SignalOutcome>(), 5);

        fv.MacdHistDelta.Should().Be(0.5m);
    }

    [Fact]
    public void Extract_VolumeRatio_ComputedCorrectly()
    {
        var snap = MakeSnap(volSma: 100m);
        var candle = MakeCandle(vol: 200);

        var fv = MlFeatureExtractor.Extract(
            snap, null, new List<IndicatorSnapshot>(), candle,
            MakeRegime(), SignalDirection.BUY, 70, new List<SignalOutcome>(), 5);

        fv.VolumeRatio.Should().Be(2m);
    }

    [Fact]
    public void Extract_VolumeRatio_Zero_When_Sma20IsZero()
    {
        var snap = MakeSnap(volSma: 0m);

        var fv = MlFeatureExtractor.Extract(
            snap, null, new List<IndicatorSnapshot>(), MakeCandle(),
            MakeRegime(), SignalDirection.BUY, 70, new List<SignalOutcome>(), 5);

        fv.VolumeRatio.Should().Be(0);
    }

    [Fact]
    public void Extract_DistanceToEma20Pct_Computed()
    {
        var snap = MakeSnap(closeMid: 2100, ema20: 2090);

        var fv = MlFeatureExtractor.Extract(
            snap, null, new List<IndicatorSnapshot>(), MakeCandle(),
            MakeRegime(), SignalDirection.BUY, 70, new List<SignalOutcome>(), 5);

        fv.DistanceToEma20Pct.Should().BeApproximately(10m / 2100m, 0.0001m);
    }

    [Fact]
    public void Extract_DiDifferential_And_DiRatio_Computed()
    {
        var snap = MakeSnap(plusDi: 30m, minusDi: 10m);

        var fv = MlFeatureExtractor.Extract(
            snap, null, new List<IndicatorSnapshot>(), MakeCandle(),
            MakeRegime(), SignalDirection.BUY, 70, new List<SignalOutcome>(), 5);

        fv.DiDifferential.Should().Be(20m);
        fv.DiRatio.Should().Be(3m);
    }

    [Fact]
    public void Extract_Slope3_Uses_RecentSnaps()
    {
        var snaps = new List<IndicatorSnapshot>
        {
            MakeSnap(ema20: 2100m, hourOffset: 0),
            MakeSnap(ema20: 2098m, hourOffset: -1),
            MakeSnap(ema20: 2096m, hourOffset: -2),
            MakeSnap(ema20: 2094m, hourOffset: -3),
            MakeSnap(ema20: 2092m, hourOffset: -4),
        };

        var fv = MlFeatureExtractor.Extract(
            snaps[0], null, snaps, MakeCandle(),
            MakeRegime(), SignalDirection.BUY, 70, new List<SignalOutcome>(), 5);

        // Slope3 = (snaps[0].Ema20 - snaps[3].Ema20) / 3 = (2100-2094)/3 = 2
        fv.Ema20Slope3.Should().Be(2m);
    }

    [Fact]
    public void Extract_Slope_Zero_When_InsufficientSnaps()
    {
        var snaps = new List<IndicatorSnapshot> { MakeSnap() };

        var fv = MlFeatureExtractor.Extract(
            snaps[0], null, snaps, MakeCandle(),
            MakeRegime(), SignalDirection.BUY, 70, new List<SignalOutcome>(), 5);

        fv.Ema20Slope3.Should().Be(0);
        fv.Ema20Slope5.Should().Be(0);
    }

    // ─── Category C: Contextual features ─────────────────

    [Theory]
    [InlineData(Regime.NEUTRAL, 0)]
    [InlineData(Regime.BULLISH, 1)]
    [InlineData(Regime.BEARISH, 2)]
    public void Extract_RegimeLabel_EncodesCorrectly(Regime regime, int expected)
    {
        var fv = MlFeatureExtractor.Extract(
            MakeSnap(), null, new List<IndicatorSnapshot>(), MakeCandle(),
            MakeRegime(regime), SignalDirection.BUY, 70, new List<SignalOutcome>(), 5);

        fv.RegimeLabel.Should().Be(expected);
    }

    [Theory]
    [InlineData(SignalDirection.BUY, 1)]
    [InlineData(SignalDirection.SELL, -1)]
    public void Extract_DirectionEncoded_Correctly(SignalDirection dir, int expected)
    {
        var fv = MlFeatureExtractor.Extract(
            MakeSnap(), null, new List<IndicatorSnapshot>(), MakeCandle(),
            MakeRegime(), dir, 70, new List<SignalOutcome>(), 5);

        fv.DirectionEncoded.Should().Be(expected);
    }

    [Fact]
    public void Extract_RuleBasedScore_PassedThrough()
    {
        var fv = MlFeatureExtractor.Extract(
            MakeSnap(), null, new List<IndicatorSnapshot>(), MakeCandle(),
            MakeRegime(), SignalDirection.BUY, 85, new List<SignalOutcome>(), 5);

        fv.RuleBasedScore.Should().Be(85);
    }

    [Fact]
    public void Extract_SessionFlags_OverlapHour()
    {
        // BaseTime is 14:00 UTC → overlap session (13-16)
        var fv = MlFeatureExtractor.Extract(
            MakeSnap(), null, new List<IndicatorSnapshot>(), MakeCandle(),
            MakeRegime(), SignalDirection.BUY, 70, new List<SignalOutcome>(), 5);

        fv.HourOfDay.Should().Be(14);
        fv.IsLondonSession.Should().BeTrue();  // 7-16
        fv.IsNySession.Should().BeTrue();        // 13-21
        fv.IsOverlap.Should().BeTrue();          // 13-16
        fv.IsAsiaSession.Should().BeFalse();     // 23-7
    }

    [Fact]
    public void Extract_SessionFlags_AsiaHour()
    {
        var snap = MakeSnap(hourOffset: -14); // 14 - 14 = 0:00 UTC
        var fv = MlFeatureExtractor.Extract(
            snap, null, new List<IndicatorSnapshot>(), MakeCandle(),
            MakeRegime(), SignalDirection.BUY, 70, new List<SignalOutcome>(), 5);

        fv.IsAsiaSession.Should().BeTrue();
        fv.IsLondonSession.Should().BeFalse();
        fv.IsNySession.Should().BeFalse();
    }

    // ─── Category D: Lookback features ───────────────────

    [Fact]
    public void Extract_OutcomeDerived_Features_Are_Computed()
    {
        var outcomes = new List<SignalOutcome>
        {
            MakeOutcome(OutcomeLabel.WIN),
            MakeOutcome(OutcomeLabel.WIN),
            MakeOutcome(OutcomeLabel.LOSS),
            MakeOutcome(OutcomeLabel.WIN),
            MakeOutcome(OutcomeLabel.LOSS),
        };

        var fv = MlFeatureExtractor.Extract(
            MakeSnap(), null, new List<IndicatorSnapshot>(), MakeCandle(),
            MakeRegime(), SignalDirection.BUY, 70, outcomes, 5);

        fv.RecentWinRate10.Should().Be(0.6m);   // 3 wins out of 5
        fv.RecentWinRate20.Should().Be(0.6m);
        fv.RecentAvgPnlR10.Should().Be(0.5m);   // all outcomes have PnlR=0.5
        fv.RecentAvgPnlR20.Should().Be(0.5m);
        fv.ConsecutiveWins.Should().Be(2);       // first two are WIN
        fv.ConsecutiveLosses.Should().Be(0);
    }

    [Fact]
    public void Extract_WinRates_Zero_When_NoOutcomes()
    {
        var fv = MlFeatureExtractor.Extract(
            MakeSnap(), null, new List<IndicatorSnapshot>(), MakeCandle(),
            MakeRegime(), SignalDirection.BUY, 70, new List<SignalOutcome>(), 5);

        fv.RecentWinRate10.Should().Be(0);
        fv.RecentWinRate20.Should().Be(0);
    }

    [Fact]
    public void Extract_BarsSinceLastSignal_PassedThrough()
    {
        var fv = MlFeatureExtractor.Extract(
            MakeSnap(), null, new List<IndicatorSnapshot>(), MakeCandle(),
            MakeRegime(), SignalDirection.BUY, 70, new List<SignalOutcome>(), 42);

        fv.BarsSinceLastSignal.Should().Be(42);
    }

    // ─── Edge cases ──────────────────────────────────────

    [Fact]
    public void Extract_ZeroDenominator_CloseMid_HandledGracefully()
    {
        var snap = MakeSnap(closeMid: 0);

        var fv = MlFeatureExtractor.Extract(
            snap, null, new List<IndicatorSnapshot>(), MakeCandle(),
            MakeRegime(), SignalDirection.BUY, 70, new List<SignalOutcome>(), 5);

        fv.Ema20MinusEma50Pct.Should().Be(0);
        fv.Atr14Pct.Should().Be(0);
        fv.SpreadPct.Should().Be(0);
    }

    [Fact]
    public void Extract_EmptyRecentSnaps_HandledGracefully()
    {
        var fv = MlFeatureExtractor.Extract(
            MakeSnap(), null, new List<IndicatorSnapshot>(), MakeCandle(),
            MakeRegime(), SignalDirection.BUY, 70, new List<SignalOutcome>(), 5);

        fv.Ema20Slope3.Should().Be(0);
        fv.AtrZscore.Should().Be(0);
        fv.PriceRange20BarsPct.Should().Be(0);
    }

    [Fact]
    public void Extract_FeatureVersion_IsV3()
    {
        MlFeatureExtractor.FeatureVersion.Should().Be("v3.0");
    }

    [Fact]
    public void Extract_Sets_EvaluationId_And_Metadata()
    {
        var snap = MakeSnap();
        var fv = MlFeatureExtractor.Extract(
            snap, null, new List<IndicatorSnapshot>(), MakeCandle(),
            MakeRegime(), SignalDirection.BUY, 70, new List<SignalOutcome>(), 5);

        fv.EvaluationId.Should().NotBeEmpty();
        fv.Symbol.Should().Be("ETHUSD");
        fv.Timeframe.Should().Be("5m");
    }

    [Fact]
    public void Extract_BodyRatio_Computed()
    {
        var candle = MakeCandle(close: 2100);
        decimal expectedBody = Math.Abs(candle.MidClose - candle.MidOpen);
        decimal expectedRange = candle.MidHigh - candle.MidLow;

        var fv = MlFeatureExtractor.Extract(
            MakeSnap(), null, new List<IndicatorSnapshot>(), candle,
            MakeRegime(), SignalDirection.BUY, 70, new List<SignalOutcome>(), 5);

        fv.BodyRatio.Should().Be(SignalEngine.ComputeBodyRatio(candle));
    }

    // ─── Category E: Market structure features ───────────

    [Fact]
    public void Extract_RangePosition_Within_0_1()
    {
        var snaps = Enumerable.Range(0, 20)
            .Select(i => MakeSnap(closeMid: 2080m + i * 2m, hourOffset: -i))
            .ToList();
        var fv = MlFeatureExtractor.Extract(
            snaps[0], null, snaps, MakeCandle(), MakeRegime(),
            SignalDirection.BUY, 70, new List<SignalOutcome>(), 5);

        fv.RangePositionPct.Should().BeGreaterThanOrEqualTo(0m);
        fv.RangePositionPct.Should().BeLessThanOrEqualTo(1m);
    }

    [Fact]
    public void Extract_DistanceTo20BarHigh_IsNonPositive()
    {
        var snaps = Enumerable.Range(0, 20)
            .Select(i => MakeSnap(closeMid: 2100m, hourOffset: -i))
            .ToList();
        var fv = MlFeatureExtractor.Extract(
            snaps[0], null, snaps, MakeCandle(), MakeRegime(),
            SignalDirection.BUY, 70, new List<SignalOutcome>(), 5);

        // close <= 20-bar high, so distance should be <= 0
        fv.DistanceTo20BarHighPct.Should().BeLessThanOrEqualTo(0m);
    }

    [Fact]
    public void Extract_SessionRangePosition_Defaults_WhenEmpty()
    {
        var fv = MlFeatureExtractor.Extract(
            MakeSnap(), null, new List<IndicatorSnapshot>(), MakeCandle(),
            MakeRegime(), SignalDirection.BUY, 70, new List<SignalOutcome>(), 5);

        // With no snaps, should default gracefully
        fv.SessionRangePositionPct.Should().BeGreaterThanOrEqualTo(0m);
        fv.SessionRangePositionPct.Should().BeLessThanOrEqualTo(1m);
    }

    // ─── Category F: Volatility regime features ──────────

    [Fact]
    public void Extract_RealizedVolatility_PositiveWithData()
    {
        var snaps = Enumerable.Range(0, 20)
            .Select(i => MakeSnap(closeMid: 2100m + (i % 3 == 0 ? 5m : -3m), hourOffset: -i))
            .ToList();
        var fv = MlFeatureExtractor.Extract(
            snaps[0], null, snaps, MakeCandle(), MakeRegime(),
            SignalDirection.BUY, 70, new List<SignalOutcome>(), 5);

        fv.RealizedVol15m.Should().BeGreaterThanOrEqualTo(0m);
        fv.RealizedVol1h.Should().BeGreaterThanOrEqualTo(0m);
    }

    [Fact]
    public void Extract_VolatilityFlags_Exclusive()
    {
        // Cannot be both compressed and expanded
        var snaps = Enumerable.Range(0, 20)
            .Select(i => MakeSnap(closeMid: 2100m + i, hourOffset: -i))
            .ToList();
        var fv = MlFeatureExtractor.Extract(
            snaps[0], null, snaps, MakeCandle(), MakeRegime(),
            SignalDirection.BUY, 70, new List<SignalOutcome>(), 5);

        // At most one can be 1
        (fv.VolatilityCompressionFlag + fv.VolatilityExpansionFlag).Should().BeLessThanOrEqualTo(1m);
    }

    [Fact]
    public void Extract_AtrPercentileRank_Between_0_And_1()
    {
        var snaps = Enumerable.Range(0, 20)
            .Select(i => MakeSnap(hourOffset: -i))
            .ToList();
        var fv = MlFeatureExtractor.Extract(
            snaps[0], null, snaps, MakeCandle(), MakeRegime(),
            SignalDirection.BUY, 70, new List<SignalOutcome>(), 5);

        fv.AtrPercentileRank.Should().BeGreaterThanOrEqualTo(0m);
        fv.AtrPercentileRank.Should().BeLessThanOrEqualTo(1m);
    }

    // ─── Category G: Signal saturation features ──────────

    [Fact]
    public void Extract_SignalSaturation_CountsCorrectly()
    {
        var signals = new List<SignalRecommendation>
        {
            new() { Symbol = "ETHUSD", Timeframe = "5m", SignalTimeUtc = BaseTime, Direction = SignalDirection.BUY, Regime = Regime.BULLISH, Reasons = [] },
            new() { Symbol = "ETHUSD", Timeframe = "5m", SignalTimeUtc = BaseTime.AddMinutes(-5), Direction = SignalDirection.BUY, Regime = Regime.BULLISH, Reasons = [] },
            new() { Symbol = "ETHUSD", Timeframe = "5m", SignalTimeUtc = BaseTime.AddMinutes(-10), Direction = SignalDirection.SELL, Regime = Regime.BEARISH, Reasons = [] },
        };
        var fv = MlFeatureExtractor.Extract(
            MakeSnap(), null, new List<IndicatorSnapshot>(), MakeCandle(),
            MakeRegime(), SignalDirection.BUY, 70, new List<SignalOutcome>(), 5,
            recentSignals: signals);

        fv.SignalsLast10Bars.Should().Be(3);
        fv.SameDirectionSignalsLast10.Should().Be(2);
        fv.OppositeDirectionSignalsLast10.Should().Be(1);
    }

    [Fact]
    public void Extract_SignalSaturation_ZeroWithNoSignals()
    {
        var fv = MlFeatureExtractor.Extract(
            MakeSnap(), null, new List<IndicatorSnapshot>(), MakeCandle(),
            MakeRegime(), SignalDirection.BUY, 70, new List<SignalOutcome>(), 5);

        fv.SignalsLast10Bars.Should().Be(0);
        fv.SameDirectionSignalsLast10.Should().Be(0);
    }

    [Fact]
    public void Extract_StopOutStats_CountsCorrectly()
    {
        var outcomes = new List<SignalOutcome>
        {
            new() { SignalId = Guid.NewGuid(), OutcomeLabel = OutcomeLabel.LOSS, SlHit = true, PnlR = -1m },
            new() { SignalId = Guid.NewGuid(), OutcomeLabel = OutcomeLabel.LOSS, SlHit = true, PnlR = -1m },
            new() { SignalId = Guid.NewGuid(), OutcomeLabel = OutcomeLabel.LOSS, SlHit = false, PnlR = -0.5m },
            new() { SignalId = Guid.NewGuid(), OutcomeLabel = OutcomeLabel.WIN, PnlR = 1.5m },
        };
        var fv = MlFeatureExtractor.Extract(
            MakeSnap(), null, new List<IndicatorSnapshot>(), MakeCandle(),
            MakeRegime(), SignalDirection.BUY, 70, outcomes, 5);

        fv.RecentStopOutCount.Should().Be(2);
        fv.RecentFalseBreakoutRate.Should().BeApproximately(2m / 3m, 0.001m);
    }

    // ─── Category H: BTC cross-asset context ─────────────

    [Fact]
    public void Extract_BtcContext_UsedWhenProvided()
    {
        var btcCtx = new BtcCrossAssetContext
        {
            BtcRecentReturn = 0.02m,
            BtcRegimeLabel = 1,
            EthBtcRelativeStrength = 0.01m
        };
        var fv = MlFeatureExtractor.Extract(
            MakeSnap(), null, new List<IndicatorSnapshot>(), MakeCandle(),
            MakeRegime(), SignalDirection.BUY, 70, new List<SignalOutcome>(), 5,
            btcContext: btcCtx);

        fv.BtcRecentReturn.Should().Be(0.02m);
        fv.BtcRegimeLabel.Should().Be(1);
        fv.EthBtcRelativeStrength.Should().Be(0.01m);
    }

    [Fact]
    public void Extract_BtcContext_DefaultsToZeroWhenNull()
    {
        var fv = MlFeatureExtractor.Extract(
            MakeSnap(), null, new List<IndicatorSnapshot>(), MakeCandle(),
            MakeRegime(), SignalDirection.BUY, 70, new List<SignalOutcome>(), 5);

        fv.BtcRecentReturn.Should().Be(0m);
        fv.BtcRegimeLabel.Should().Be(0);
        fv.EthBtcRelativeStrength.Should().Be(0m);
    }

    // ─── Backward compatibility ──────────────────────────

    [Fact]
    public void OldModelsStillWorkWithNewFeatures_MissingFeaturesDefaultToZero()
    {
        // Simulate an old model that only knows the first 59 features
        var oldFeatureNames = MlFeatureVector.FeatureNames.Take(59).ToList();
        var snap = MakeSnap();
        var fv = MlFeatureExtractor.Extract(
            snap, null, new List<IndicatorSnapshot>(), MakeCandle(),
            MakeRegime(), SignalDirection.BUY, 70, new List<SignalOutcome>(), 5);

        // ToFloatArray with old model's feature list should still work
        var arr = fv.ToFloatArray(oldFeatureNames);
        arr.Should().HaveCount(59);
    }
}
