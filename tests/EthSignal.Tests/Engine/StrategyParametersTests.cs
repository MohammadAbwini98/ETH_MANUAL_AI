using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Engine;
using FluentAssertions;

namespace EthSignal.Tests.Engine;

public class StrategyParametersTests
{
    [Fact]
    public void Default_MlMode_Is_Shadow_And_Does_Not_AutoActivate()
    {
        StrategyParameters.Default.MlMode.Should().Be(MlMode.SHADOW);
        StrategyParameters.Default.MlAutoActivateOnStartup.Should().BeFalse();
        StrategyParameters.Default.DailyLossCapPercent.Should().Be(StrategyParameters.RecommendedDailyLossCapPercent);
    }

    [Fact]
    public void Validate_Fails_When_ExitTpMultiples_AreNotAscending()
    {
        var result = (StrategyParameters.Default with
        {
            ExitTp1RMultiple = 1.5m,
            ExitTp2RMultiple = 1.0m,
            ExitTp3RMultiple = 2.0m
        }).Validate();

        result.Should().Contain("strictly ascending");
    }

    [Fact]
    public void Validate_Fails_When_IntradayTimeoutBars_IsNotPositive()
    {
        var result = (StrategyParameters.Default with { IntradayTimeoutBars = 0 }).Validate();

        result.Should().Contain("IntradayTimeoutBars");
    }

    [Fact]
    public void ExitPolicy_Uses_Configured_MinStopDistancePct()
    {
        var policy = ExitEngine.BuildPolicy(StrategyParameters.Default with
        {
            MinStopDistancePct = 0.0035m
        });

        policy.MinStopDistancePct.Should().Be(0.0035m);
    }

    [Fact]
    public void ResolveForTimeframe_Applies_Exact_Profile_With_Bucket_Fallback_Without_Mutating_Base_Params()
    {
        var parameters = StrategyParameters.Default with
        {
            TimeframeProfiles = TimeframeStrategyProfileSet.Recommended
        };

        var fast = parameters.ResolveForTimeframe("1m");
        var mid = parameters.ResolveForTimeframe("15m");
        var longTf = parameters.ResolveForTimeframe("1h");

        fast.StopAtrMultiplier.Should().Be(parameters.TimeframeProfiles.M1.StopAtrMultiplier);
        fast.ExitIntradayMinAtrTpMultiplier.Should().Be(parameters.TimeframeProfiles.Fast.ExitIntradayMinAtrTpMultiplier);
        fast.ScalpCooldownBars.Should().Be(parameters.TimeframeProfiles.M1.ScalpCooldownBars);
        mid.ConfidenceBuyThreshold.Should().Be(parameters.TimeframeProfiles.M15.ConfidenceBuyThreshold);
        mid.ExitStructureBufferAtrMultiplier.Should().Be(parameters.TimeframeProfiles.Mid.ExitStructureBufferAtrMultiplier);
        longTf.TargetRMultiple.Should().Be(parameters.TimeframeProfiles.H1.TargetRMultiple);
        parameters.StopAtrMultiplier.Should().Be(2.0m);
    }

    [Fact]
    public void Validate_Fails_When_Long_Profile_Creates_Invalid_Stop_Range()
    {
        var parameters = StrategyParameters.Default with
        {
            TimeframeProfiles = StrategyParameters.Default.TimeframeProfiles with
            {
                Long = StrategyParameters.Default.TimeframeProfiles.Long with
                {
                    MinStopDistancePct = 0.06m
                }
            }
        };

        var result = parameters.Validate();

        result.Should().Contain("1h timeframe profile invalid");
    }

    [Fact]
    public void ExitPolicy_Uses_HigherTimeframe_Target_Band_For_Long_Bucket()
    {
        var policy = ExitEngine.BuildPolicy(StrategyParameters.Default, "1h");

        policy.IntradayMinAtrTpMultiplier.Should().Be(StrategyParameters.Default.ResolveForTimeframe("1h").ExitHigherTfMinAtrTpMultiplier);
        policy.IntradayMaxAtrTpMultiplier.Should().Be(StrategyParameters.Default.ResolveForTimeframe("1h").ExitHigherTfMaxAtrTpMultiplier);
    }

    [Fact]
    public void EnsureRecommendedTimeframeProfiles_Hydrates_Recommended_Set_When_Profiles_Are_Empty()
    {
        var normalized = StrategyParameters.Default
            .EnsureRecommendedTimeframeProfiles();

        normalized.TimeframeProfiles.IsEffectivelyEmpty().Should().BeFalse();
        normalized.ResolveForTimeframe("5m").ConfidenceBuyThreshold
            .Should().Be(TimeframeStrategyProfileSet.Recommended.M5.ConfidenceBuyThreshold);
    }

    [Fact]
    public void EnsureProductionSafeDefaults_Normalizes_Legacy_Daily_Loss_Cap_And_Hydrates_Timeframe_Profiles()
    {
        var normalized = (StrategyParameters.Default with
        {
            DailyLossCapPercent = decimal.MaxValue,
            TimeframeProfiles = TimeframeStrategyProfileSet.Default
        }).EnsureProductionSafeDefaults();

        normalized.DailyLossCapPercent.Should().Be(StrategyParameters.RecommendedDailyLossCapPercent);
        normalized.TimeframeProfiles.IsEffectivelyEmpty().Should().BeFalse();
        normalized.ResolveForTimeframe("1h").NeutralRegimePolicy
            .Should().Be(NeutralRegimePolicy.AllowReducedRiskEntriesInNeutral);
        normalized.ResolveForTimeframe("1h").TargetRMultiple
            .Should().Be(TimeframeStrategyProfileSet.Recommended.H1.TargetRMultiple);
    }

    [Fact]
    public void AdaptiveClamp_Preserves_Base_DailyLossCap()
    {
        var baseParameters = StrategyParameters.Default with { DailyLossCapPercent = 4.5m };
        var clamped = ParameterBands.Clamp(baseParameters with { ConfidenceBuyThreshold = 999 }, baseParameters);

        clamped.DailyLossCapPercent.Should().Be(4.5m);
    }

    [Fact]
    public void Recommended_Timeframe_Profiles_Allow_Reduced_Risk_Entries_In_Neutral_For_Higher_Timeframes()
    {
        var parameters = StrategyParameters.Default with
        {
            TimeframeProfiles = TimeframeStrategyProfileSet.Recommended
        };

        parameters.ResolveForTimeframe("5m").NeutralRegimePolicy
            .Should().Be(NeutralRegimePolicy.AllowReducedRiskEntriesInNeutral);
        parameters.ResolveForTimeframe("15m").NeutralRegimePolicy
            .Should().Be(NeutralRegimePolicy.AllowReducedRiskEntriesInNeutral);
        parameters.ResolveForTimeframe("1h").NeutralRegimePolicy
            .Should().Be(NeutralRegimePolicy.AllowReducedRiskEntriesInNeutral);
        parameters.ResolveForTimeframe("4h").NeutralRegimePolicy
            .Should().Be(NeutralRegimePolicy.AllowReducedRiskEntriesInNeutral);
    }
}
