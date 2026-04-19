using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Engine;
using EthSignal.Infrastructure.Engine.ML;
using FluentAssertions;

namespace EthSignal.Tests.Engine;

/// <summary>P4 tests: 5M Entry Signal Engine.</summary>
public class SignalEngineTests
{
    private static IndicatorSnapshot MakeSnap(
        decimal closeMid, decimal ema20, decimal rsi, decimal macdHist,
        decimal adx, decimal vwap, decimal volSma20, decimal spread) => new()
        {
            Symbol = "ETHUSD",
            Timeframe = "5m",
            CandleOpenTimeUtc = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero),
            Ema20 = ema20,
            Ema50 = ema20 - 5,
            Rsi14 = rsi,
            Macd = 0.5m,
            MacdSignal = 0.3m,
            MacdHist = macdHist,
            Atr14 = 10m,
            Adx14 = adx,
            PlusDi = 25,
            MinusDi = 15,
            VolumeSma20 = volSma20,
            Vwap = vwap,
            Spread = spread,
            CloseMid = closeMid,
            IsProvisional = false
        };

    private static RichCandle MakeCandle(decimal close, decimal vol) => new()
    {
        OpenTime = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero),
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

    private static RegimeResult MakeRegime(Regime regime) => new()
    {
        Symbol = "ETHUSD",
        CandleOpenTimeUtc = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero),
        Regime = regime,
        RegimeScore = 5,
        TriggeredConditions = ["all"],
        DisqualifyingConditions = []
    };

    private static MlPrediction MakePrediction(decimal calibratedWinProbability, int recommendedThreshold = 60) => new()
    {
        PredictionId = Guid.NewGuid(),
        EvaluationId = Guid.NewGuid(),
        ModelVersion = "model-v1",
        ModelType = "outcome_predictor",
        RawWinProbability = calibratedWinProbability,
        CalibratedWinProbability = calibratedWinProbability,
        PredictionConfidence = (int)Math.Round(calibratedWinProbability * 100m),
        RecommendedThreshold = recommendedThreshold,
        ExpectedValueR = 0.5m,
        InferenceLatencyUs = 25,
        IsActive = true,
        Mode = MlMode.ACTIVE,
        InferenceMode = MlInferenceMode.TRAINED_ACTIVE
    };

    private static (SignalRecommendation Rec, SignalDecision Dec) MakePrecomputedSignal(int confidenceScore = 70)
    {
        var rec = new SignalRecommendation
        {
            Symbol = "ETHUSD",
            Timeframe = "5m",
            SignalTimeUtc = new DateTimeOffset(2026, 3, 17, 10, 5, 0, TimeSpan.Zero),
            Direction = SignalDirection.BUY,
            EntryPrice = 2100m,
            TpPrice = 2115m,
            SlPrice = 2090m,
            ConfidenceScore = confidenceScore,
            Regime = Regime.BULLISH,
            StrategyVersion = "v3.1",
            Reasons = ["rule-based signal"]
        };

        var dec = new SignalDecision
        {
            Symbol = "ETHUSD",
            Timeframe = "5m",
            DecisionTimeUtc = new DateTimeOffset(2026, 3, 17, 10, 5, 0, TimeSpan.Zero),
            BarTimeUtc = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero),
            DecisionType = SignalDirection.BUY,
            OutcomeCategory = OutcomeCategory.SIGNAL_GENERATED,
            ReasonCodes = [],
            ReasonDetails = ["rule-based signal"],
            IndicatorSnapshot = new Dictionary<string, decimal>(),
            ConfidenceScore = confidenceScore,
            UsedRegime = Regime.BULLISH,
            UsedRegimeTimestamp = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero),
            SourceMode = SourceMode.LIVE,
            ParameterSetId = "v3.1"
        };

        return (rec, dec);
    }

    /// <summary>P4-T1: BUY signal generation with bullish regime + valid 5m setup.</summary>
    [Fact]
    public void BUY_Signal_Generated_When_All_Conditions_Met()
    {
        var regime = MakeRegime(Regime.BULLISH);
        // Close > EMA20, RSI rising in zone, MACD hist positive, ADX >= 18, Close > VWAP, volume high, spread low
        var snap = MakeSnap(closeMid: 2100, ema20: 2090, rsi: 52, macdHist: 0.5m,
            adx: 22, vwap: 2080, volSma20: 100, spread: 1m);
        var prevSnap = snap with { Rsi14 = 45, MacdHist = 0.2m };
        var candle = MakeCandle(2100, vol: 150);

        var result = SignalEngine.Evaluate("ETHUSD", regime, snap, prevSnap, candle);

        result.Direction.Should().Be(SignalDirection.BUY);
        result.ConfidenceScore.Should().BeGreaterThanOrEqualTo(70);
    }

    /// <summary>P4-T2: SELL signal generation with bearish regime + valid 5m setup.</summary>
    [Fact]
    public void SELL_Signal_Generated_When_All_Conditions_Met()
    {
        var regime = MakeRegime(Regime.BEARISH);
        // Close < EMA20, RSI falling, MACD hist negative, ADX >= 18, Close < VWAP, volume high, spread low
        var snap = MakeSnap(closeMid: 1900, ema20: 1910, rsi: 48, macdHist: -0.5m,
            adx: 22, vwap: 1920, volSma20: 100, spread: 1m);
        var prevSnap = snap with { Rsi14 = 55, MacdHist = -0.2m };
        var candle = MakeCandle(1900, vol: 150);

        var result = SignalEngine.Evaluate("ETHUSD", regime, snap, prevSnap, candle);

        result.Direction.Should().Be(SignalDirection.SELL);
        result.ConfidenceScore.Should().BeGreaterThanOrEqualTo(70);
    }

    /// <summary>P4-T3: NEUTRAL regime with BlockAll policy blocks signal generation.</summary>
    [Fact]
    public void Neutral_Regime_Returns_NO_TRADE()
    {
        var regime = MakeRegime(Regime.NEUTRAL);
        var snap = MakeSnap(closeMid: 2100, ema20: 2090, rsi: 52, macdHist: 0.5m,
            adx: 22, vwap: 2080, volSma20: 100, spread: 1m);
        var candle = MakeCandle(2100, vol: 150);
        var p = StrategyParameters.Default with
        {
            NeutralRegimePolicy = NeutralRegimePolicy.BlockAllEntriesInNeutral
        };

        var result = SignalEngine.Evaluate("ETHUSD", regime, snap, null, candle, p);

        result.Direction.Should().Be(SignalDirection.NO_TRADE);
        result.Reasons.Should().Contain(r => r.Contains("NEUTRAL"));
    }

    /// <summary>P4-T4: Weak score (partial conditions) returns NO_TRADE.</summary>
    [Fact]
    public void Weak_Score_Returns_NO_TRADE()
    {
        var regime = MakeRegime(Regime.BULLISH);
        // Close > EMA20 but RSI not rising (no prevSnap), MACD hist negative, ADX low
        var snap = MakeSnap(closeMid: 2100, ema20: 2090, rsi: 52, macdHist: -0.5m,
            adx: 12, vwap: 2080, volSma20: 100, spread: 1m);
        var candle = MakeCandle(2100, vol: 80);
        // Use a high threshold to ensure weak scores fail
        var p = StrategyParameters.Default with { ConfidenceBuyThreshold = 85 };

        var result = SignalEngine.Evaluate("ETHUSD", regime, snap, null, candle, p);

        result.Direction.Should().Be(SignalDirection.NO_TRADE);
    }

    /// <summary>P4-T5: Explainability — reasons contain condition details.</summary>
    [Fact]
    public void Reasons_Include_All_Conditions()
    {
        var regime = MakeRegime(Regime.BULLISH);
        var snap = MakeSnap(closeMid: 2100, ema20: 2090, rsi: 52, macdHist: 0.5m,
            adx: 22, vwap: 2080, volSma20: 100, spread: 1m);
        var prevSnap = snap with { Rsi14 = 45, MacdHist = 0.2m };
        var candle = MakeCandle(2100, vol: 150);

        var result = SignalEngine.Evaluate("ETHUSD", regime, snap, prevSnap, candle);

        result.Reasons.Should().NotBeEmpty();
        result.Reasons.Should().Contain(r => r.Contains("Regime"));
        result.Reasons.Should().Contain(r => r.Contains("ullback") || r.Contains("reclaim"));
        result.Reasons.Should().Contain(r => r.Contains("RSI"));
        result.Reasons.Should().Contain(r => r.Contains("MACD"));
        result.Reasons.Should().Contain(r => r.Contains("ADX"));
        result.Reasons.Should().Contain(r => r.Contains("Score"));
    }

    [Fact]
    public void Wide_Spread_Blocks_Signal()
    {
        var regime = MakeRegime(Regime.BULLISH);
        // Spread too wide relative to price
        var snap = MakeSnap(closeMid: 2100, ema20: 2090, rsi: 52, macdHist: 0.5m,
            adx: 22, vwap: 2080, volSma20: 100, spread: 10m); // 10/2100 > 0.3%
        var prevSnap = snap with { Rsi14 = 45 };
        var candle = MakeCandle(2100, vol: 150);

        var result = SignalEngine.Evaluate("ETHUSD", regime, snap, prevSnap, candle);

        result.Direction.Should().Be(SignalDirection.NO_TRADE);
        result.Reasons.Should().Contain(r => r.Contains("Spread") && r.Contains("wide"));
    }

    [Fact]
    public void Body_Ratio_Computed_Correctly()
    {
        var candle = new RichCandle
        {
            OpenTime = DateTimeOffset.UtcNow,
            BidOpen = 100,
            BidHigh = 110,
            BidLow = 90,
            BidClose = 108,
            AskOpen = 101,
            AskHigh = 111,
            AskLow = 91,
            AskClose = 109,
            Volume = 100
        };
        // MidOpen = 100.5, MidHigh = 110.5, MidLow = 90.5, MidClose = 108.5
        // Body = |108.5 - 100.5| = 8, Range = 110.5 - 90.5 = 20
        // Ratio = 8/20 = 0.4
        var ratio = SignalEngine.ComputeBodyRatio(candle);
        ratio.Should().Be(0.4m);
    }

    /// <summary>
    /// Accuracy-first mode: base gate uses the accuracy-first min probability,
    /// not the legacy 0.55 minimum.
    /// </summary>
    [Fact]
    public void AccuracyFirst_BaseGate_UsesAccuracyFirstMinWinProbability()
    {
        var regime = MakeRegime(Regime.BULLISH);
        // Mid-session London, strong ADX → no weak-context bump
        var snap = MakeSnap(closeMid: 2100, ema20: 2090, rsi: 52, macdHist: 0.5m,
            adx: 25, vwap: 2080, volSma20: 100, spread: 1m)
            with { CandleOpenTimeUtc = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero) };

        var p = StrategyParameters.Default with
        {
            MlAccuracyFirstMode = true,
            MlAccuracyFirstMinWinProbability = 0.60m,
            MlWeakContextMinWinProbabilityBump = 0.03m,
            AccuracyFirstLowAdxThreshold = 18m
        };

        var effective = SignalEngine.ComputeEffectiveMinWinProbability(p, regime, snap, out var reason);

        effective.Should().Be(0.60m);
        reason.Should().Contain("accuracy-first base");
    }

    /// <summary>
    /// Accuracy-first mode: weak-context setup (NEUTRAL regime OR low ADX OR
    /// weak Asia session) bumps the gate above the base.
    /// </summary>
    [Fact]
    public void AccuracyFirst_WeakContext_BumpsGateAboveBase()
    {
        var regime = MakeRegime(Regime.NEUTRAL);
        var snap = MakeSnap(closeMid: 2100, ema20: 2090, rsi: 52, macdHist: 0.5m,
            adx: 12, vwap: 2080, volSma20: 100, spread: 1m)
            with { CandleOpenTimeUtc = new DateTimeOffset(2026, 3, 17, 2, 0, 0, TimeSpan.Zero) };

        var p = StrategyParameters.Default with
        {
            MlAccuracyFirstMode = true,
            MlAccuracyFirstMinWinProbability = 0.60m,
            MlWeakContextMinWinProbabilityBump = 0.03m,
            AccuracyFirstLowAdxThreshold = 18m
        };

        var effective = SignalEngine.ComputeEffectiveMinWinProbability(p, regime, snap, out var reason);

        effective.Should().Be(0.63m);
        reason.Should().Contain("weak context");
    }

    /// <summary>
    /// With accuracy-first mode OFF, the effective gate falls back to the
    /// legacy MlMinWinProbability parameter.
    /// </summary>
    [Fact]
    public void AccuracyFirst_Disabled_FallsBackToLegacyMlMinWinProbability()
    {
        var regime = MakeRegime(Regime.BULLISH);
        var snap = MakeSnap(closeMid: 2100, ema20: 2090, rsi: 52, macdHist: 0.5m,
            adx: 25, vwap: 2080, volSma20: 100, spread: 1m);

        var p = StrategyParameters.Default with
        {
            MlAccuracyFirstMode = false,
            MlMinWinProbability = 0.52m
        };

        var effective = SignalEngine.ComputeEffectiveMinWinProbability(p, regime, snap, out var reason);

        effective.Should().Be(0.52m);
        reason.Should().Contain("legacy");
    }

    [Fact]
    public void EvaluateWithMl_ShadowMode_AnnotatesButKeepsRuleSignal()
    {
        var regime = MakeRegime(Regime.BULLISH);
        var snap = MakeSnap(closeMid: 2100, ema20: 2090, rsi: 52, macdHist: 0.5m,
            adx: 25, vwap: 2080, volSma20: 100, spread: 1m);
        var candle = MakeCandle(2100, vol: 150);
        var p = StrategyParameters.Default with { MlMode = MlMode.SHADOW };
        var mlPrediction = MakePrediction(0.78m, recommendedThreshold: 65);

        var (rec, dec) = SignalEngine.EvaluateWithMl(
            "ETHUSD", regime, snap, null, candle, p, mlPrediction, null,
            preComputed: MakePrecomputedSignal());

        rec.Direction.Should().Be(SignalDirection.BUY);
        dec.DecisionType.Should().Be(SignalDirection.BUY);
        dec.MlPrediction.Should().NotBeNull();
        dec.BlendedConfidence.Should().BeGreaterThan(0);
        dec.EffectiveThreshold.Should().Be(65);
    }

    [Fact]
    public void EvaluateWithMl_ActiveMode_Filters_WhenWinProbabilityIsBelowGate()
    {
        var regime = MakeRegime(Regime.BULLISH);
        var snap = MakeSnap(closeMid: 2100, ema20: 2090, rsi: 52, macdHist: 0.5m,
            adx: 25, vwap: 2080, volSma20: 100, spread: 1m);
        var candle = MakeCandle(2100, vol: 150);
        var p = StrategyParameters.Default with
        {
            MlMode = MlMode.ACTIVE,
            MlAccuracyFirstMode = false,
            MlMinWinProbability = 0.55m
        };
        var mlPrediction = MakePrediction(0.40m, recommendedThreshold: 50);

        var (rec, dec) = SignalEngine.EvaluateWithMl(
            "ETHUSD", regime, snap, null, candle, p, mlPrediction, null,
            preComputed: MakePrecomputedSignal());

        rec.Direction.Should().Be(SignalDirection.NO_TRADE);
        dec.DecisionType.Should().Be(SignalDirection.NO_TRADE);
        dec.LifecycleState.Should().Be(SignalLifecycleState.ML_FILTERED);
        dec.ReasonCodes.Should().Contain(RejectReasonCode.ML_GATE_FAILED);
        dec.FinalBlockReason.Should().Contain("ML gate failed");
    }

    [Fact]
    public void EvaluateWithMl_ActiveMode_KeepsSignal_WhenMlConfirms()
    {
        var regime = MakeRegime(Regime.BULLISH);
        var snap = MakeSnap(closeMid: 2100, ema20: 2090, rsi: 52, macdHist: 0.5m,
            adx: 25, vwap: 2080, volSma20: 100, spread: 1m);
        var candle = MakeCandle(2100, vol: 150);
        var p = StrategyParameters.Default with
        {
            MlMode = MlMode.ACTIVE,
            MlAccuracyFirstMode = false,
            MlMinWinProbability = 0.55m,
            MlConfidenceBlendWeight = 0.5m
        };
        var mlPrediction = MakePrediction(0.80m, recommendedThreshold: 55);

        var (rec, dec) = SignalEngine.EvaluateWithMl(
            "ETHUSD", regime, snap, null, candle, p, mlPrediction, null,
            preComputed: MakePrecomputedSignal());

        rec.Direction.Should().Be(SignalDirection.BUY);
        dec.DecisionType.Should().Be(SignalDirection.BUY);
        dec.OutcomeCategory.Should().Be(OutcomeCategory.SIGNAL_GENERATED);
        dec.BlendedConfidence.Should().BeGreaterThanOrEqualTo(55);
    }
}
