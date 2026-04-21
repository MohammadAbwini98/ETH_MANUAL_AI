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
}
