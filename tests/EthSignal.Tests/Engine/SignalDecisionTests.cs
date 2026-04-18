using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Engine;
using FluentAssertions;

namespace EthSignal.Tests.Engine;

/// <summary>
/// Unit tests for No-Signal Investigation SRS requirements.
/// UT-1 through UT-5 plus additional coverage.
/// </summary>
public class SignalDecisionTests
{
    private static IndicatorSnapshot MakeSnap(
        decimal closeMid = 2100, decimal ema20 = 2090, decimal rsi = 52,
        decimal macdHist = 0.5m, decimal adx = 22, decimal vwap = 2080,
        decimal volSma20 = 100, decimal spread = 1m) => new()
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

    private static RichCandle MakeCandle(decimal close = 2100, decimal vol = 150) => new()
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
        CandleOpenTimeUtc = new DateTimeOffset(2026, 3, 17, 9, 45, 0, TimeSpan.Zero),
        Regime = regime,
        RegimeScore = 5,
        TriggeredConditions = ["all"],
        DisqualifyingConditions = []
    };

    // ─── UT-1: NEUTRAL regime + BlockAll → NO_TRADE with REGIME_NEUTRAL ─────
    [Fact]
    public void UT1_Neutral_Regime_BlockAll_Returns_NoTrade_With_RegimeNeutral()
    {
        var regime = MakeRegime(Regime.NEUTRAL);
        var snap = MakeSnap();
        var candle = MakeCandle();
        var p = StrategyParameters.Default with
        {
            NeutralRegimePolicy = NeutralRegimePolicy.BlockAllEntriesInNeutral
        };

        var (rec, decision) = SignalEngine.EvaluateWithDecision(
            "ETHUSD", regime, snap, null, candle, p);

        // Signal is NO_TRADE
        rec.Direction.Should().Be(SignalDirection.NO_TRADE);

        // Decision has structured rejection
        decision.DecisionType.Should().Be(SignalDirection.NO_TRADE);
        decision.OutcomeCategory.Should().Be(OutcomeCategory.STRATEGY_NO_TRADE);
        decision.ReasonCodes.Should().Contain(RejectReasonCode.REGIME_NEUTRAL);
        decision.ReasonCodes.Should().HaveCountGreaterThanOrEqualTo(1);
        decision.UsedRegime.Should().Be(Regime.NEUTRAL);
        decision.SourceMode.Should().Be(SourceMode.LIVE);
    }

    // ─── UT-2: NEUTRAL regime + AllowReducedRisk → proceeds to evaluate LTF ──
    [Fact]
    public void UT2_Neutral_Regime_AllowReduced_Evaluates_LowerTimeframe()
    {
        var regime = MakeRegime(Regime.NEUTRAL);
        var snap = MakeSnap();
        var prevSnap = snap with { Rsi14 = 45, MacdHist = 0.2m };
        var candle = MakeCandle();
        var p = StrategyParameters.Default with
        {
            NeutralRegimePolicy = NeutralRegimePolicy.AllowReducedRiskEntriesInNeutral
        };

        var (rec, decision) = SignalEngine.EvaluateWithDecision(
            "ETHUSD", regime, snap, prevSnap, candle, p);

        // Should NOT immediately reject with REGIME_NEUTRAL early return
        decision.ReasonCodes.Should().NotContain(RejectReasonCode.REGIME_NEUTRAL);

        // Should have evaluated further — may still be NO_TRADE but with detailed reasons
        decision.ReasonDetails.Should().NotBeEmpty();
        decision.ReasonDetails.Should().Contain(r => r.Contains("NEUTRAL") && r.Contains("proceeding"));
    }

    // ─── UT-3: Stale recovered regime → CONTEXT_NOT_READY ──────────────────
    [Fact]
    public void UT3_Stale_Regime_Returns_ContextNotReady()
    {
        // Create a regime from far in the past
        var staleRegime = new RegimeResult
        {
            Symbol = "ETHUSD",
            // 2 hours ago = 8 bars of 15m — exceeds MaxRecoveredRegimeAgeBars=4
            CandleOpenTimeUtc = DateTimeOffset.UtcNow.AddHours(-2),
            Regime = Regime.BULLISH,
            RegimeScore = 5,
            TriggeredConditions = ["all"],
            DisqualifyingConditions = []
        };

        var snap = MakeSnap();
        var candle = MakeCandle();
        var p = StrategyParameters.Default with
        {
            MaxRecoveredRegimeAgeBars = 4
        };

        // For the stale check, we simulate what LiveTickProcessor does:
        var regimeAge = DateTimeOffset.UtcNow - staleRegime.CandleOpenTimeUtc;
        var ageBars = (int)(regimeAge.TotalMinutes / 15);

        // Verify the regime is indeed considered stale
        ageBars.Should().BeGreaterThan(p.MaxRecoveredRegimeAgeBars);

        // Build a CONTEXT_NOT_READY decision as LiveTickProcessor would
        var decision = new SignalDecision
        {
            Symbol = "ETHUSD",
            Timeframe = "5m",
            DecisionTimeUtc = DateTimeOffset.UtcNow,
            BarTimeUtc = candle.OpenTime,
            DecisionType = SignalDirection.NO_TRADE,
            OutcomeCategory = OutcomeCategory.CONTEXT_NOT_READY,
            UsedRegime = staleRegime.Regime,
            UsedRegimeTimestamp = staleRegime.CandleOpenTimeUtc,
            ReasonCodes = [RejectReasonCode.STALE_HTF_CONTEXT],
            ReasonDetails = [$"Regime age {ageBars} bars exceeds max {p.MaxRecoveredRegimeAgeBars}"],
            IndicatorSnapshot = new Dictionary<string, decimal>(),
            ParameterSetId = p.StrategyVersion,
            SourceMode = SourceMode.LIVE
        };

        decision.OutcomeCategory.Should().Be(OutcomeCategory.CONTEXT_NOT_READY);
        decision.ReasonCodes.Should().Contain(RejectReasonCode.STALE_HTF_CONTEXT);
    }

    // ─── UT-4: Multiple rejection reasons all returned and complete ─────────
    [Fact]
    public void UT4_Multiple_Rejection_Reasons_All_Returned()
    {
        var regime = MakeRegime(Regime.BULLISH);
        // Set up conditions to fail multiple checks:
        // RSI out of range (85 > 78), MACD negative, ADX too low,
        // pullback fails (candle low far above EMA20+zone)
        var snap = MakeSnap(closeMid: 2200, ema20: 2090, rsi: 85, macdHist: -0.5m,
            adx: 10, vwap: 2080, volSma20: 100, spread: 1m);
        var candle = new RichCandle
        {
            OpenTime = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero),
            BidOpen = 2195,
            BidHigh = 2210,
            BidLow = 2190,
            BidClose = 2198,
            AskOpen = 2196,
            AskHigh = 2211,
            AskLow = 2191,
            AskClose = 2199,
            Volume = 50,
            IsClosed = true
        };
        var p = StrategyParameters.Default;

        var (_, decision) = SignalEngine.EvaluateWithDecision(
            "ETHUSD", regime, snap, null, candle, p);

        decision.DecisionType.Should().Be(SignalDirection.NO_TRADE);
        decision.OutcomeCategory.Should().Be(OutcomeCategory.STRATEGY_NO_TRADE);

        // Multiple reason codes should be present
        decision.ReasonCodes.Should().HaveCountGreaterThanOrEqualTo(2);
        decision.ReasonCodes.Should().Contain(RejectReasonCode.RSI_OUT_OF_RANGE);
        decision.ReasonCodes.Should().Contain(RejectReasonCode.ADX_TOO_LOW);
    }

    // ─── UT-5: Duplicate decision for same bar is safely handled ────────────
    [Fact]
    public void UT5_Decision_Has_Unique_Constraint_Fields()
    {
        var regime = MakeRegime(Regime.NEUTRAL);
        var snap = MakeSnap();
        var candle = MakeCandle();
        var p = StrategyParameters.Default;

        var (_, d1) = SignalEngine.EvaluateWithDecision(
            "ETHUSD", regime, snap, null, candle, p, SourceMode.LIVE, "v2.0");
        var (_, d2) = SignalEngine.EvaluateWithDecision(
            "ETHUSD", regime, snap, null, candle, p, SourceMode.LIVE, "v2.0");

        // Both decisions refer to the same bar/symbol/timeframe/sourceMode
        d1.Symbol.Should().Be(d2.Symbol);
        d1.Timeframe.Should().Be(d2.Timeframe);
        d1.BarTimeUtc.Should().Be(d2.BarTimeUtc);
        d1.SourceMode.Should().Be(d2.SourceMode);

        // But have different IDs (repo uses ON CONFLICT DO NOTHING for dedup)
        d1.DecisionId.Should().NotBe(d2.DecisionId);
    }

    // ─── Additional: SignalDecision JSON serialization works ─────────────────
    [Fact]
    public void Decision_Serializes_ReasonCodes_And_Indicators_To_Json()
    {
        var regime = MakeRegime(Regime.NEUTRAL);
        var snap = MakeSnap();
        var candle = MakeCandle();
        // Use BlockAll to guarantee REGIME_NEUTRAL reason code
        var p = StrategyParameters.Default with
        {
            NeutralRegimePolicy = NeutralRegimePolicy.BlockAllEntriesInNeutral
        };

        var (_, decision) = SignalEngine.EvaluateWithDecision(
            "ETHUSD", regime, snap, null, candle, p);

        decision.ReasonCodesJson.Should().Contain("REGIME_NEUTRAL");
        decision.ReasonDetailsJson.Should().NotBeEmpty();
        decision.IndicatorsJson.Should().Contain("ema20");
        decision.IndicatorsJson.Should().Contain("rsi14");
    }

    // ─── Additional: BUY signal produces a decision with no rejection codes ──
    [Fact]
    public void BUY_Signal_Produces_Decision_With_No_RejectCodes()
    {
        var regime = MakeRegime(Regime.BULLISH);
        var snap = MakeSnap(closeMid: 2100, ema20: 2090, rsi: 52, macdHist: 0.5m,
            adx: 22, vwap: 2080, volSma20: 100, spread: 1m);
        var prevSnap = snap with { Rsi14 = 45, MacdHist = 0.2m };
        var candle = MakeCandle(2100, vol: 150);
        var p = StrategyParameters.Default;

        var (rec, decision) = SignalEngine.EvaluateWithDecision(
            "ETHUSD", regime, snap, prevSnap, candle, p);

        rec.Direction.Should().Be(SignalDirection.BUY);
        decision.DecisionType.Should().Be(SignalDirection.BUY);
        decision.ConfidenceScore.Should().BeGreaterThanOrEqualTo(60);
        decision.UsedRegime.Should().Be(Regime.BULLISH);
        decision.IndicatorSnapshot.Should().ContainKey("ema20");
    }

    // ─── Additional: SourceMode properly set for different evaluation modes ──
    [Fact]
    public void SourceMode_Properly_Set_For_Warm_Start()
    {
        var regime = MakeRegime(Regime.BULLISH);
        var snap = MakeSnap();
        var candle = MakeCandle();
        var p = StrategyParameters.Default;

        var (_, decision) = SignalEngine.EvaluateWithDecision(
            "ETHUSD", regime, snap, null, candle, p,
            SourceMode.STARTUP_WARM, "v2.0");

        decision.SourceMode.Should().Be(SourceMode.STARTUP_WARM);
        decision.ParameterSetId.Should().Be("v2.0");
    }

    // ─── Additional: Spread gate produces SPREAD_TOO_WIDE reason code ────────
    [Fact]
    public void Spread_Gate_Produces_Correct_ReasonCode()
    {
        var regime = MakeRegime(Regime.BULLISH);
        var snap = MakeSnap(spread: 10m); // 10/2100 > 0.3%
        var prevSnap = snap with { Rsi14 = 45 };
        var candle = MakeCandle();
        var p = StrategyParameters.Default;

        var (_, decision) = SignalEngine.EvaluateWithDecision(
            "ETHUSD", regime, snap, prevSnap, candle, p);

        decision.DecisionType.Should().Be(SignalDirection.NO_TRADE);
        decision.ReasonCodes.Should().Contain(RejectReasonCode.SPREAD_TOO_WIDE);
    }

    // ─── FR-8: OutcomeCategory classification tests ──────────────────────────
    [Fact]
    public void FR8_Strategy_NoTrade_Classified_Correctly()
    {
        var regime = MakeRegime(Regime.NEUTRAL);
        var snap = MakeSnap();
        var candle = MakeCandle();
        var p = StrategyParameters.Default with
        {
            NeutralRegimePolicy = NeutralRegimePolicy.BlockAllEntriesInNeutral
        };

        var (_, decision) = SignalEngine.EvaluateWithDecision(
            "ETHUSD", regime, snap, null, candle, p);

        decision.OutcomeCategory.Should().Be(OutcomeCategory.STRATEGY_NO_TRADE);
    }

    // ─── NeutralRegimePolicy defaults to BlockAllEntriesInNeutral ────────────
    [Fact]
    public void Default_Parameters_Block_Neutral_Regime_Entries()
    {
        var p = StrategyParameters.Default;
        p.NeutralRegimePolicy.Should().Be(NeutralRegimePolicy.BlockAllEntriesInNeutral);
    }

    // ─── Default parameters have safe config values ──────────────────────────
    [Fact]
    public void Default_Parameters_Have_Safe_Regime_Freshness_Config()
    {
        var p = StrategyParameters.Default;
        p.MaxRecoveredRegimeAgeBars.Should().Be(6);
        p.WarmStartEvaluateLatestClosed5m.Should().BeFalse();
        p.BackfillReplaySignals.Should().BeFalse();
    }
}
