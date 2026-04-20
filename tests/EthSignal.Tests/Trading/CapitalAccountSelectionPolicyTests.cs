using EthSignal.Infrastructure.Trading;
using FluentAssertions;

namespace EthSignal.Tests.Trading;

public sealed class CapitalAccountSelectionPolicyTests
{
    [Fact]
    public void ResolveRequiredDemoAccount_WhenDemoAiExistsAndIsDemo_SelectsIt()
    {
        var accounts = new[]
        {
            Account("live-1", "LIVE", isDemo: false, preferred: true),
            Account("demo-1", "DEMOAI", isDemo: true)
        };

        var resolved = CapitalAccountSelectionPolicy.ResolveRequiredDemoAccount(
            accounts,
            "DEMOAI",
            isDemoEnvironment: true,
            resolvedAtUtc: new DateTimeOffset(2026, 4, 20, 9, 0, 0, TimeSpan.Zero));

        resolved.Account.AccountId.Should().Be("demo-1");
        resolved.Account.AccountName.Should().Be("DEMOAI");
        resolved.Account.IsDemo.Should().BeTrue();
    }

    [Fact]
    public void ResolveRequiredDemoAccount_WhenDemoAiMissing_FailsSafely()
    {
        var accounts = new[]
        {
            Account("live-1", "LIVE", isDemo: false, preferred: true)
        };

        var act = () => CapitalAccountSelectionPolicy.ResolveRequiredDemoAccount(
            accounts,
            "DEMOAI",
            isDemoEnvironment: true,
            resolvedAtUtc: DateTimeOffset.UtcNow);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*DEMOAI*not found*");
    }

    [Fact]
    public void ResolveRequiredDemoAccount_WhenOnlyLiveAccountExists_FailsSafely()
    {
        var accounts = new[]
        {
            Account("live-1", "DEMOAI", isDemo: false, preferred: true)
        };

        var act = () => CapitalAccountSelectionPolicy.ResolveRequiredDemoAccount(
            accounts,
            "DEMOAI",
            isDemoEnvironment: true,
            resolvedAtUtc: DateTimeOffset.UtcNow);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*live-account marker*");
    }

    [Fact]
    public void ResolveRequiredDemoAccount_WhenFirstAccountIsLiveButDemoAiAppearsLater_SelectsDemoAi()
    {
        var accounts = new[]
        {
            Account("live-1", "PRIMARYLIVE", isDemo: false, preferred: true),
            Account("demo-2", "DEMOAI", isDemo: true, preferred: false)
        };

        var resolved = CapitalAccountSelectionPolicy.ResolveRequiredDemoAccount(
            accounts,
            "DEMOAI",
            isDemoEnvironment: true,
            resolvedAtUtc: DateTimeOffset.UtcNow);

        resolved.Account.AccountId.Should().Be("demo-2");
        resolved.Account.AccountName.Should().Be("DEMOAI");
    }

    private static CapitalBrokerAccount Account(string id, string name, bool? isDemo, bool preferred = false) => new()
    {
        AccountId = id,
        AccountName = name,
        Status = "ENABLED",
        Preferred = preferred,
        Currency = "USD",
        IsDemo = isDemo,
        AccountType = isDemo == true ? "demo" : "live"
    };
}