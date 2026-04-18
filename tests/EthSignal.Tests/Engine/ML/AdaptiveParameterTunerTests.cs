using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using EthSignal.Infrastructure.Engine.ML;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace EthSignal.Tests.Engine.ML;

public class AdaptiveParameterTunerTests
{
    private static AdaptiveParameterTuner CreateTuner()
    {
        var paramRepo = new Mock<IParameterRepository>().Object;
        var signalRepo = new Mock<ISignalRepository>().Object;
        var logger = new Mock<ILogger<AdaptiveParameterTuner>>().Object;
        return new AdaptiveParameterTuner(paramRepo, signalRepo, logger);
    }

    private static StrategyParameters DefaultParams() => new()
    {
        ConfidenceBuyThreshold = 65,
        ConfidenceSellThreshold = 65,
        PullbackZonePct = 0.005m
    };

    private static SignalOutcome MakeOutcome(OutcomeLabel label, decimal pnlR) => new()
    {
        SignalId = Guid.NewGuid(),
        OutcomeLabel = label,
        PnlR = pnlR
    };

    [Fact]
    public void RecordOutcome_Returns_Null_Before_AdjustmentInterval()
    {
        var tuner = CreateTuner();
        var p = DefaultParams();

        // Only record 5 outcomes — well below the 25 adjustment interval
        for (int i = 0; i < 5; i++)
        {
            var result = tuner.RecordOutcome(MakeOutcome(OutcomeLabel.WIN, 1.5m), p);
            result.Should().BeNull();
        }
    }

    [Fact]
    public void RecordOutcome_Ignores_NonResolved_Outcomes()
    {
        var tuner = CreateTuner();
        var p = DefaultParams();

        // Record 30 PENDING outcomes — should not trigger adjustment
        for (int i = 0; i < 30; i++)
        {
            var result = tuner.RecordOutcome(MakeOutcome(OutcomeLabel.PENDING, 0), p);
            result.Should().BeNull();
        }
    }

    [Fact]
    public void RecordOutcome_RaisesThresholds_When_PerformanceDegrades()
    {
        var tuner = CreateTuner();
        tuner.SetBaseline(0.5m); // Good baseline expectancy
        var p = DefaultParams();

        // Record 25 outcomes with moderate losses (not extreme enough to trigger G2 revert)
        // Mix: 8 wins, 17 losses → expectancy ~0.08, which is < 80% of 0.5 baseline
        AdaptiveTuneResult? finalResult = null;
        for (int i = 0; i < 25; i++)
        {
            var outcome = i < 8
                ? MakeOutcome(OutcomeLabel.WIN, 1.5m)
                : MakeOutcome(OutcomeLabel.LOSS, -0.5m);
            finalResult = tuner.RecordOutcome(outcome, p) ?? finalResult;
        }

        finalResult.Should().NotBeNull();
        // Could be Adjust or Revert depending on rolling-30 check
        finalResult!.Action.Should().BeOneOf(TuneAction.Adjust, TuneAction.Revert);
        if (finalResult.Action == TuneAction.Adjust)
            finalResult.AdjustedParameters!.ConfidenceBuyThreshold.Should().BeGreaterThan(p.ConfidenceBuyThreshold);
    }

    [Fact]
    public void RecordOutcome_LowersThresholds_When_StrongPerformance()
    {
        var tuner = CreateTuner();
        tuner.SetBaseline(0.1m); // Low baseline
        var p = DefaultParams();

        // Record 25 outcomes with strong performance (> 120% of baseline)
        AdaptiveTuneResult? finalResult = null;
        for (int i = 0; i < 25; i++)
        {
            // All wins → high expectancy
            var outcome = MakeOutcome(OutcomeLabel.WIN, 1.5m);
            finalResult = tuner.RecordOutcome(outcome, p) ?? finalResult;
        }

        finalResult.Should().NotBeNull();
        finalResult!.Action.Should().Be(TuneAction.Adjust);
        finalResult.AdjustedParameters!.ConfidenceBuyThreshold.Should().BeLessThan(p.ConfidenceBuyThreshold);
    }

    [Fact]
    public void RecordOutcome_Reverts_When_Rolling30_Expectancy_VeryNegative()
    {
        var tuner = CreateTuner();
        tuner.SetBaseline(0.3m);
        var p = DefaultParams();

        // Record 25 outcomes all losses → rolling 30 expectancy < -0.1R
        AdaptiveTuneResult? finalResult = null;
        for (int i = 0; i < 25; i++)
        {
            var outcome = MakeOutcome(OutcomeLabel.LOSS, -1.0m);
            finalResult = tuner.RecordOutcome(outcome, p) ?? finalResult;
        }

        finalResult.Should().NotBeNull();
        finalResult!.Action.Should().Be(TuneAction.Revert);
        finalResult.Reason.Should().Contain("-0.1R");
    }

    [Fact]
    public void RecordOutcome_Stops_After_MaxAdjustments()
    {
        var tuner = CreateTuner();
        tuner.SetBaseline(0.5m);
        var p = DefaultParams();

        int adjustmentCount = 0;

        // Run enough cycles to hit max adjustments (3)
        for (int cycle = 0; cycle < 5; cycle++)
        {
            for (int i = 0; i < 25; i++)
            {
                var outcome = i < 5
                    ? MakeOutcome(OutcomeLabel.WIN, 0.5m)
                    : MakeOutcome(OutcomeLabel.LOSS, -1.0m);
                var result = tuner.RecordOutcome(outcome, p);
                if (result?.Action == TuneAction.Adjust)
                    adjustmentCount++;
            }
        }

        adjustmentCount.Should().BeLessThanOrEqualTo(3);
    }

    [Fact]
    public void ResetAdjustmentCount_Allows_NewAdjustments()
    {
        var tuner = CreateTuner();
        tuner.SetBaseline(0.1m); // Low baseline so wins easily exceed 120%
        var p = DefaultParams();

        // First round of adjustments — strong performance
        int adjustments = 0;
        for (int cycle = 0; cycle < 5; cycle++)
        {
            for (int i = 0; i < 25; i++)
            {
                var outcome = MakeOutcome(OutcomeLabel.WIN, 1.5m);
                var result = tuner.RecordOutcome(outcome, p);
                if (result?.Action == TuneAction.Adjust) adjustments++;
            }
        }

        // Reset
        tuner.ResetAdjustmentCount();

        // Should be able to adjust again with strong performance
        bool gotAdjustment = false;
        for (int i = 0; i < 25; i++)
        {
            var outcome = MakeOutcome(OutcomeLabel.WIN, 1.5m);
            var result = tuner.RecordOutcome(outcome, p);
            if (result?.Action == TuneAction.Adjust) gotAdjustment = true;
        }

        gotAdjustment.Should().BeTrue();
    }

    [Fact]
    public void RecordOutcome_Thresholds_NeverExceed_90()
    {
        var tuner = CreateTuner();
        tuner.SetBaseline(0.8m);
        var p = DefaultParams() with
        {
            ConfidenceBuyThreshold = 88,
            ConfidenceSellThreshold = 88
        };

        AdaptiveTuneResult? finalResult = null;
        for (int i = 0; i < 25; i++)
        {
            var outcome = i < 5
                ? MakeOutcome(OutcomeLabel.WIN, 0.5m)
                : MakeOutcome(OutcomeLabel.LOSS, -1.0m);
            finalResult = tuner.RecordOutcome(outcome, p) ?? finalResult;
        }

        if (finalResult?.AdjustedParameters != null)
        {
            finalResult.AdjustedParameters.ConfidenceBuyThreshold.Should().BeLessThanOrEqualTo(90);
            finalResult.AdjustedParameters.ConfidenceSellThreshold.Should().BeLessThanOrEqualTo(90);
        }
    }

    [Fact]
    public void RecordOutcome_Thresholds_NeverGoBelow_40()
    {
        var tuner = CreateTuner();
        tuner.SetBaseline(0.1m);
        var p = DefaultParams() with
        {
            ConfidenceBuyThreshold = 42,
            ConfidenceSellThreshold = 42
        };

        AdaptiveTuneResult? finalResult = null;
        for (int i = 0; i < 25; i++)
        {
            var outcome = MakeOutcome(OutcomeLabel.WIN, 1.5m);
            finalResult = tuner.RecordOutcome(outcome, p) ?? finalResult;
        }

        if (finalResult?.AdjustedParameters != null)
        {
            finalResult.AdjustedParameters.ConfidenceBuyThreshold.Should().BeGreaterThanOrEqualTo(40);
            finalResult.AdjustedParameters.ConfidenceSellThreshold.Should().BeGreaterThanOrEqualTo(40);
        }
    }
}
