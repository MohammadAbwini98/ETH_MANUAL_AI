using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Engine.ML;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace EthSignal.Tests.Engine.ML;

public class SignalFrequencyManagerTests
{
    private static SignalFrequencyManager CreateManager()
    {
        return new SignalFrequencyManager(new Mock<ILogger<SignalFrequencyManager>>().Object);
    }

    private static StrategyParameters DefaultParams(bool dynamicEnabled = true) => new()
    {
        MlMode = MlMode.ACTIVE,
        MlDynamicThresholdsEnabled = dynamicEnabled,
        MlDynamicThresholdMin = 40,
        MlDynamicThresholdMax = 90,
        MlDynamicThresholdMaxDelta = 5,
        MlMinWinProbability = 0.50m,
        MlOverrideMandatoryGates = false,
        ConfidenceBuyThreshold = 65,
        ConfidenceSellThreshold = 65,
        NeutralRegimePolicy = NeutralRegimePolicy.AllowMlGatedEntriesInNeutral
    };

    private static RegimeResult MakeRegime(Regime regime = Regime.BULLISH, int score = 5) => new()
    {
        Symbol = "ETHUSD",
        CandleOpenTimeUtc = DateTimeOffset.UtcNow,
        Regime = regime,
        RegimeScore = score,
        TriggeredConditions = [],
        DisqualifyingConditions = []
    };

    // ─── Dynamic threshold tests ─────────────────────────

    [Fact]
    public void GetDynamicThreshold_Returns_Static_When_Disabled()
    {
        var mgr = CreateManager();
        var p = DefaultParams(dynamicEnabled: false);

        var result = mgr.GetDynamicThreshold(Regime.BULLISH, 30, 0.005m, 14, 0.55m, "5m", p);

        result.Source.Should().Be("static");
        result.BuyThreshold.Should().Be(p.ConfidenceBuyThreshold);
    }

    [Fact]
    public void GetDynamicThreshold_LooksUp_ByRegime_Adx_Session()
    {
        var mgr = CreateManager();
        var p = DefaultParams();

        // BULLISH, ADX >= 30 (high), hour 14 (overlap)
        var result = mgr.GetDynamicThreshold(Regime.BULLISH, 32, 0.005m, 14, 0.50m, "5m", p);

        result.Source.Should().StartWith("dynamic:");
        result.BuyThreshold.Should().BeInRange(40, 90);
    }

    [Fact]
    public void GetDynamicThreshold_HighWinRate_LowersThreshold()
    {
        var mgr = CreateManager();
        var p = DefaultParams();

        // Low win rate → threshold raised by 10
        var low = mgr.GetDynamicThreshold(Regime.BULLISH, 32, 0.005m, 14, 0.35m, "5m", p);
        // Reset for comparison — high win rate lowers by 5
        var mgr2 = CreateManager();
        var high = mgr2.GetDynamicThreshold(Regime.BULLISH, 32, 0.005m, 14, 0.65m, "5m", p);

        // High win rate threshold should be <= low win rate threshold
        high.BuyThreshold.Should().BeLessThanOrEqualTo(low.BuyThreshold);
    }

    [Fact]
    public void GetDynamicThreshold_Clamped_To_ConfiguredRange()
    {
        var mgr = CreateManager();
        var p = DefaultParams();

        var result = mgr.GetDynamicThreshold(Regime.NEUTRAL, 10, 0.005m, 2, 0.30m, "5m", p);

        result.BuyThreshold.Should().BeGreaterThanOrEqualTo(p.MlDynamicThresholdMin);
        result.BuyThreshold.Should().BeLessThanOrEqualTo(p.MlDynamicThresholdMax);
    }

    [Fact]
    public void GetDynamicThreshold_MaxDelta_Smoothing()
    {
        var mgr = CreateManager();
        var p = DefaultParams();

        // First call establishes baseline
        var r1 = mgr.GetDynamicThreshold(Regime.BULLISH, 32, 0.005m, 14, 0.50m, "5m", p);

        // Second call with very different conditions — delta should be limited
        var r2 = mgr.GetDynamicThreshold(Regime.NEUTRAL, 10, 0.005m, 2, 0.30m, "5m", p);

        Math.Abs(r2.BuyThreshold - r1.BuyThreshold).Should().BeLessThanOrEqualTo(p.MlDynamicThresholdMaxDelta);
    }

    // ─── Neutral regime ML gating tests ──────────────────

    [Fact]
    public void ShouldAllowNeutralEntry_True_When_HighProbAndScore()
    {
        var mgr = CreateManager();
        var p = DefaultParams();
        var prediction = new MlPrediction
        {
            PredictionId = Guid.NewGuid(),
            EvaluationId = Guid.NewGuid(),
            ModelVersion = "v1.0",
            ModelType = "outcome_predictor",
            RawWinProbability = 0.65m,
            CalibratedWinProbability = 0.65m,
            PredictionConfidence = 70,
            RecommendedThreshold = 60,
            IsActive = true,
            Mode = MlMode.ACTIVE
        };
        var regime = MakeRegime(Regime.NEUTRAL, score: 4);

        mgr.ShouldAllowNeutralEntry(prediction, regime, p).Should().BeTrue();
    }

    [Fact]
    public void ShouldAllowNeutralEntry_False_When_LowProbability()
    {
        var mgr = CreateManager();
        var p = DefaultParams();
        var prediction = new MlPrediction
        {
            PredictionId = Guid.NewGuid(),
            EvaluationId = Guid.NewGuid(),
            ModelVersion = "v1.0",
            ModelType = "outcome_predictor",
            RawWinProbability = 0.45m,
            CalibratedWinProbability = 0.45m, // Below 0.60 threshold
            PredictionConfidence = 50,
            RecommendedThreshold = 60,
            IsActive = true,
            Mode = MlMode.ACTIVE
        };

        mgr.ShouldAllowNeutralEntry(prediction, MakeRegime(Regime.NEUTRAL, 4), p).Should().BeFalse();
    }

    [Fact]
    public void ShouldAllowNeutralEntry_False_When_LowRegimeScore()
    {
        var mgr = CreateManager();
        var p = DefaultParams();
        var prediction = new MlPrediction
        {
            PredictionId = Guid.NewGuid(),
            EvaluationId = Guid.NewGuid(),
            ModelVersion = "v1.0",
            ModelType = "outcome_predictor",
            RawWinProbability = 0.70m,
            CalibratedWinProbability = 0.70m,
            PredictionConfidence = 70,
            RecommendedThreshold = 60,
            IsActive = true,
            Mode = MlMode.ACTIVE
        };

        // Score 2 < required 3
        mgr.ShouldAllowNeutralEntry(prediction, MakeRegime(Regime.NEUTRAL, 2), p).Should().BeFalse();
    }

    [Fact]
    public void ShouldAllowNeutralEntry_False_When_WrongPolicy()
    {
        var mgr = CreateManager();
        var p = DefaultParams() with
        {
            NeutralRegimePolicy = NeutralRegimePolicy.BlockAllEntriesInNeutral
        };
        var prediction = new MlPrediction
        {
            PredictionId = Guid.NewGuid(),
            EvaluationId = Guid.NewGuid(),
            ModelVersion = "v1.0",
            ModelType = "outcome_predictor",
            RawWinProbability = 0.80m,
            CalibratedWinProbability = 0.80m,
            PredictionConfidence = 80,
            RecommendedThreshold = 60,
            IsActive = true,
            Mode = MlMode.ACTIVE
        };

        mgr.ShouldAllowNeutralEntry(prediction, MakeRegime(Regime.NEUTRAL, 5), p).Should().BeFalse();
    }

    [Fact]
    public void ShouldAllowNeutralEntry_False_When_NoPrediction()
    {
        var mgr = CreateManager();
        var p = DefaultParams();

        mgr.ShouldAllowNeutralEntry(null, MakeRegime(Regime.NEUTRAL, 5), p).Should().BeFalse();
    }

    [Fact]
    public void ShouldAllowNeutralEntry_False_When_MlDisabled()
    {
        var mgr = CreateManager();
        var p = DefaultParams() with { MlMode = MlMode.DISABLED };
        var prediction = new MlPrediction
        {
            PredictionId = Guid.NewGuid(),
            EvaluationId = Guid.NewGuid(),
            ModelVersion = "v1.0",
            ModelType = "outcome_predictor",
            RawWinProbability = 0.80m,
            CalibratedWinProbability = 0.80m,
            PredictionConfidence = 80,
            RecommendedThreshold = 60,
            IsActive = true,
            Mode = MlMode.ACTIVE
        };

        mgr.ShouldAllowNeutralEntry(prediction, MakeRegime(Regime.NEUTRAL, 5), p).Should().BeFalse();
    }

    // ─── Mandatory gate relaxation tests ─────────────────

    [Fact]
    public void CanRelaxMandatoryGates_False_When_Disabled()
    {
        var mgr = CreateManager();
        var p = DefaultParams() with { MlOverrideMandatoryGates = false };
        var prediction = new MlPrediction
        {
            PredictionId = Guid.NewGuid(),
            EvaluationId = Guid.NewGuid(),
            ModelVersion = "v1.0",
            ModelType = "outcome_predictor",
            RawWinProbability = 0.90m,
            CalibratedWinProbability = 0.90m,
            PredictionConfidence = 90,
            RecommendedThreshold = 60,
            IsActive = true,
            Mode = MlMode.ACTIVE
        };

        mgr.CanRelaxMandatoryGates(prediction, p).Should().BeFalse();
    }

    [Fact]
    public void CanRelaxMandatoryGates_True_When_HighProbability()
    {
        var mgr = CreateManager();
        var p = DefaultParams() with
        {
            MlOverrideMandatoryGates = true,
            MlMinWinProbability = 0.50m
        };
        var prediction = new MlPrediction
        {
            PredictionId = Guid.NewGuid(),
            EvaluationId = Guid.NewGuid(),
            ModelVersion = "v1.0",
            ModelType = "outcome_predictor",
            RawWinProbability = 0.70m,
            CalibratedWinProbability = 0.70m, // > 0.50 + 0.15 = 0.65
            PredictionConfidence = 70,
            RecommendedThreshold = 60,
            IsActive = true,
            Mode = MlMode.ACTIVE
        };

        mgr.CanRelaxMandatoryGates(prediction, p).Should().BeTrue();
    }

    [Fact]
    public void CanRelaxMandatoryGates_False_When_NotActiveMode()
    {
        var mgr = CreateManager();
        var p = DefaultParams() with
        {
            MlMode = MlMode.SHADOW,
            MlOverrideMandatoryGates = true
        };
        var prediction = new MlPrediction
        {
            PredictionId = Guid.NewGuid(),
            EvaluationId = Guid.NewGuid(),
            ModelVersion = "v1.0",
            ModelType = "outcome_predictor",
            RawWinProbability = 0.90m,
            CalibratedWinProbability = 0.90m,
            PredictionConfidence = 90,
            RecommendedThreshold = 60,
            IsActive = true,
            Mode = MlMode.SHADOW
        };

        mgr.CanRelaxMandatoryGates(prediction, p).Should().BeFalse();
    }

    // ─── Lookup table loading ────────────────────────────

    [Fact]
    public void LoadLookupTable_Replaces_Defaults()
    {
        var mgr = CreateManager();
        var p = DefaultParams();

        // Get the default threshold first
        var defaultResult = mgr.GetDynamicThreshold(Regime.BULLISH, 35, 0.005m, 14, 0.50m, "5m", p);

        // Load custom lookup with lower thresholds
        var mgr2 = CreateManager();
        mgr2.LoadLookupTable(new Dictionary<string, int>
        {
            ["BULLISH_high_overlap"] = 40,
            ["BULLISH_high_any"] = 42
        });

        var result = mgr2.GetDynamicThreshold(Regime.BULLISH, 35, 0.005m, 14, 0.50m, "5m", p);
        // After loading custom lookup with 40, result should be <= default
        result.BuyThreshold.Should().BeLessThanOrEqualTo(defaultResult.BuyThreshold);
    }
}
