namespace EthSignal.Infrastructure.Trading;

public sealed record CapitalBrokerAccount
{
    public required string AccountId { get; init; }
    public required string AccountName { get; init; }
    public required string Status { get; init; }
    public string Currency { get; init; } = "USD";
    public bool Preferred { get; init; }
    public bool? IsDemo { get; init; }
    public string? AccountType { get; init; }
    public string? EnvironmentName { get; init; }
}

public sealed record CapitalResolvedAccountContext
{
    public required CapitalBrokerAccount Account { get; init; }
    public required string SelectionSource { get; init; }
    public required DateTimeOffset ResolvedAtUtc { get; init; }
}

public static class CapitalAccountSelectionPolicy
{
    public static CapitalResolvedAccountContext ResolveRequiredDemoAccount(
        IReadOnlyList<CapitalBrokerAccount> accounts,
        string requiredAccountName,
        bool isDemoEnvironment,
        DateTimeOffset resolvedAtUtc)
    {
        if (!isDemoEnvironment)
        {
            throw new InvalidOperationException(
                "Capital demo account selection requires the demo API environment.");
        }

        var enabledAccounts = accounts
            .Where(account => string.Equals(account.Status, "ENABLED", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (enabledAccounts.Count == 0)
            throw new InvalidOperationException("No enabled Capital.com accounts are available.");

        var matches = enabledAccounts
            .Where(account => string.Equals(account.AccountName, requiredAccountName, StringComparison.Ordinal))
            .ToList();

        if (matches.Count == 0)
        {
            throw new InvalidOperationException(
                $"Required Capital demo account '{requiredAccountName}' was not found among enabled accounts.");
        }

        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                $"Multiple enabled Capital accounts were returned with the exact name '{requiredAccountName}'. Aborting to avoid ambiguous account selection.");
        }

        var target = matches[0];
        if (target.IsDemo == false)
        {
            throw new InvalidOperationException(
                $"Capital account '{requiredAccountName}' was returned with a live-account marker. Refusing to use it for demo execution.");
        }

        var selectionSource = target.IsDemo == true
            ? "accounts.exact-name+explicit-demo"
            : "accounts.exact-name+demo-environment";

        return new CapitalResolvedAccountContext
        {
            Account = target,
            SelectionSource = selectionSource,
            ResolvedAtUtc = resolvedAtUtc
        };
    }
}