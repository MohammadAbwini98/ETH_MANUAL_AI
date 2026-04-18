using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Notifications;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EthSignal.Tests.Infrastructure;

public class TelegramNotifierTests
{
    // ─── TC-1: Constructor — empty token disables sending ─────────────
    [Fact]
    public async Task SendAsync_EmptyToken_ReturnsWithoutSending()
    {
        // Arrange: notifier with blank token
        var notifier = new TelegramNotifier("", [1495017760L], NullLogger<TelegramNotifier>.Instance);

        // Act + Assert: no exception, completes immediately
        await notifier.Invoking(n => n.SendAsync("test")).Should().NotThrowAsync();
    }

    // ─── TC-2: Constructor — empty chat list disables sending ──────────
    [Fact]
    public async Task SendAsync_NoChatIds_ReturnsWithoutSending()
    {
        var notifier = new TelegramNotifier("sometoken", [], NullLogger<TelegramNotifier>.Instance);
        await notifier.Invoking(n => n.SendAsync("test")).Should().NotThrowAsync();
    }

    // ─── TC-3: Formatter — SystemStart contains required fields ────────
    [Fact]
    public void SystemStart_ContainsRequiredFields()
    {
        var msg = TelegramMessageFormatter.SystemStart("ETHUSD", "Production");

        msg.Should().Contain("STARTED");
        msg.Should().Contain("ETHUSD");
        msg.Should().Contain("Production");
        msg.Should().Contain("🟢");
    }

    // ─── TC-4: Formatter — SystemStop contains required fields ─────────
    [Fact]
    public void SystemStop_ContainsRequiredFields()
    {
        var msg = TelegramMessageFormatter.SystemStop("ETHUSD", "Graceful shutdown");

        msg.Should().Contain("STOPPED");
        msg.Should().Contain("ETHUSD");
        msg.Should().Contain("Graceful shutdown");
        msg.Should().Contain("🔴");
    }

    // ─── TC-5: Formatter — BUY signal contains all required data fields
    [Fact]
    public void NewSignal_Buy_ContainsAllRequiredFields()
    {
        var signal = MakeSignal(SignalDirection.BUY);
        var decision = MakeDecision(signal, mlPrediction: null);
        var regime = MakeRegime(Regime.BULLISH);

        var msg = TelegramMessageFormatter.NewSignal(signal, decision, regime);

        msg.Should().Contain("BUY");
        msg.Should().Contain("2062");          // entry price
        msg.Should().Contain("SIGNAL_GENERATED");
        msg.Should().Contain("BULLISH");
        msg.Should().Contain("📈");
        msg.Should().Contain("Take Profit");
        msg.Should().Contain("Stop Loss");
        msg.Should().Contain("R:R Ratio");
        msg.Should().Contain("Score");
        msg.Should().Contain("Risk");
        msg.Should().Contain("Regime");
    }

    // ─── TC-6: Formatter — SELL signal direction label ─────────────────
    [Fact]
    public void NewSignal_Sell_ContainsSellLabel()
    {
        var signal = MakeSignal(SignalDirection.SELL);
        var decision = MakeDecision(signal, mlPrediction: null);

        var msg = TelegramMessageFormatter.NewSignal(signal, decision, null);

        msg.Should().Contain("SELL");
        msg.Should().Contain("📉");
        msg.Should().NotContain("BUY");
    }

    // ─── TC-7: Formatter — ML SHADOW block rendered when prediction present
    [Fact]
    public void NewSignal_WithMlPrediction_RendersMLBlock()
    {
        var signal = MakeSignal(SignalDirection.BUY);
        var ml = new MlPrediction
        {
            PredictionId = Guid.NewGuid(),
            EvaluationId = Guid.NewGuid(),
            ModelVersion = "v2026_test",
            ModelType = "outcome_predictor",
            RawWinProbability = 0.643m,
            CalibratedWinProbability = 0.643m,
            PredictionConfidence = 68,
            RecommendedThreshold = 62,
            ExpectedValueR = 0.47m,
            InferenceLatencyUs = 120,
            IsActive = false,
            Mode = MlMode.SHADOW
        };
        var decision = MakeDecision(signal, ml);

        var msg = TelegramMessageFormatter.NewSignal(signal, decision, null);

        msg.Should().Contain("64.3%");         // win probability
        msg.Should().Contain("68%");           // calibrated confidence
        msg.Should().Contain("62");            // threshold
        msg.Should().Contain("+0.47R");        // expected R
        msg.Should().Contain("v2026_test");    // model version
        msg.Should().Contain("SHADOW");
        msg.Should().Contain("annotation only");
    }

    // ─── TC-8: Formatter — ML ACTIVE block shows "gating decisions" ────
    [Fact]
    public void NewSignal_WithActiveMl_ShowsActiveLabel()
    {
        var signal = MakeSignal(SignalDirection.BUY);
        var ml = new MlPrediction
        {
            PredictionId = Guid.NewGuid(),
            EvaluationId = Guid.NewGuid(),
            ModelVersion = "v_active",
            ModelType = "outcome_predictor",
            RawWinProbability = 0.72m,
            CalibratedWinProbability = 0.72m,
            PredictionConfidence = 75,
            RecommendedThreshold = 65,
            ExpectedValueR = 0.8m,
            InferenceLatencyUs = 100,
            IsActive = true,
            Mode = MlMode.ACTIVE
        };
        var decision = MakeDecision(signal, ml);

        var msg = TelegramMessageFormatter.NewSignal(signal, decision, null);

        msg.Should().Contain("ACTIVE");
        msg.Should().Contain("gating decisions");
    }

    // ─── TC-9: Formatter — null regime shows UNKNOWN ───────────────────
    [Fact]
    public void NewSignal_NullRegime_ShowsUnknown()
    {
        var signal = MakeSignal(SignalDirection.BUY);
        var decision = MakeDecision(signal, null);

        var msg = TelegramMessageFormatter.NewSignal(signal, decision, null);

        msg.Should().Contain("UNKNOWN");
    }

    // ─── TC-10: Formatter — reject codes rendered ─────────────────────
    [Fact]
    public void NewSignal_WithRejectCodes_RendersRejectBlock()
    {
        var signal = MakeSignal(SignalDirection.BUY);
        var decision = MakeDecision(signal, null, new[]
        {
            RejectReasonCode.ADX_TOO_LOW,
            RejectReasonCode.BODY_RATIO_TOO_SMALL
        });

        var msg = TelegramMessageFormatter.NewSignal(signal, decision, null);

        msg.Should().Contain("ADX_TOO_LOW");
        msg.Should().Contain("BODY_RATIO_TOO_SMALL");
    }

    // ─── TC-11: Formatter — HTML special chars in reasons are escaped ──
    [Fact]
    public void NewSignal_ReasonsWithHtmlChars_AreEscaped()
    {
        var signal = MakeSignal(SignalDirection.BUY);
        var decision = MakeDecision(signal, null, [],
            reasons: ["Score < threshold & direction > 0"]);

        var msg = TelegramMessageFormatter.NewSignal(signal, decision, null);

        msg.Should().Contain("&lt;");
        msg.Should().Contain("&amp;");
        msg.Should().Contain("&gt;");
        msg.Should().NotContain("<threshold");
    }

    // ─── TC-12: Formatter — blended confidence shown when present ──────
    [Fact]
    public void NewSignal_BlendedConfidence_RenderedWhenSet()
    {
        var signal = MakeSignal(SignalDirection.BUY);
        var decision = MakeDecision(signal, null) with { BlendedConfidence = 71 };

        var msg = TelegramMessageFormatter.NewSignal(signal, decision, null);

        msg.Should().Contain("Blended Conf");
        msg.Should().Contain("71%");
    }

    // ─── TC-13: Formatter — no blended confidence section when null ────
    [Fact]
    public void NewSignal_NoBlendedConfidence_SectionOmitted()
    {
        var signal = MakeSignal(SignalDirection.BUY);
        var decision = MakeDecision(signal, null); // BlendedConfidence is null

        var msg = TelegramMessageFormatter.NewSignal(signal, decision, null);

        msg.Should().NotContain("Blended Conf");
    }

    // ─── TC-14: Formatter — R:R ratio computed correctly ───────────────
    [Fact]
    public void NewSignal_RRRatio_ComputedCorrectly()
    {
        // Entry=2062, TP=2070 (8 pts), SL=2058 (4 pts) → RR = 2.0
        var signal = MakeSignal(SignalDirection.BUY, entry: 2062m, tp: 2070m, sl: 2058m);
        var decision = MakeDecision(signal, null);

        var msg = TelegramMessageFormatter.NewSignal(signal, decision, null);

        msg.Should().Contain("2:1");
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private static SignalRecommendation MakeSignal(
        SignalDirection dir,
        decimal entry = 2062.45m,
        decimal tp = 2069.33m,
        decimal sl = 2058.92m) =>
        new()
        {
            Symbol = "ETHUSD",
            Timeframe = "5m",
            SignalTimeUtc = DateTimeOffset.UtcNow,
            Direction = dir,
            EntryPrice = entry,
            TpPrice = tp,
            SlPrice = sl,
            RiskPercent = 0.5m,
            RiskUsd = 0.25m,
            ConfidenceScore = 72,
            Regime = Regime.BULLISH,
            StrategyVersion = "v3.0",
            Reasons = ["Regime aligned (+20)", "Pullback confirmed (+20)", "RSI in zone (+15)"]
        };

    private static SignalDecision MakeDecision(
        SignalRecommendation signal,
        MlPrediction? mlPrediction,
        IEnumerable<RejectReasonCode>? codes = null,
        IEnumerable<string>? reasons = null) =>
        new()
        {
            Symbol = signal.Symbol,
            Timeframe = signal.Timeframe,
            DecisionTimeUtc = signal.SignalTimeUtc,
            BarTimeUtc = signal.SignalTimeUtc,
            DecisionType = signal.Direction,
            OutcomeCategory = OutcomeCategory.SIGNAL_GENERATED,
            UsedRegime = Regime.BULLISH,
            ReasonCodes = (codes ?? []).ToList(),
            ReasonDetails = (reasons ?? signal.Reasons).ToList(),
            IndicatorSnapshot = new Dictionary<string, decimal>(),
            MlPrediction = mlPrediction,
            SourceMode = SourceMode.LIVE
        };

    private static RegimeResult MakeRegime(Regime r) =>
        new()
        {
            Symbol = "ETHUSD",
            CandleOpenTimeUtc = DateTimeOffset.UtcNow,
            Regime = r,
            RegimeScore = 4,
            TriggeredConditions = ["EMA alignment", "ADX > 20", "HH/HL structure"],
            DisqualifyingConditions = []
        };
}
