using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Engine;
using FluentAssertions;

namespace EthSignal.Tests.Engine;

public class StrategyParametersTests
{
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
}
