namespace EthSignal.Domain.Models;

/// <summary>Status lifecycle for a parameter set.</summary>
public enum ParameterSetStatus { Draft, Candidate, Active, Retired, Rejected }

/// <summary>Persisted envelope around a StrategyParameters instance.</summary>
public sealed record StrategyParameterSet
{
    public long Id { get; init; }
    public required string StrategyVersion { get; init; }
    public required string ParameterHash { get; init; }
    public required StrategyParameters Parameters { get; init; }
    public ParameterSetStatus Status { get; init; } = ParameterSetStatus.Draft;
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? CreatedBy { get; init; }
    public DateTimeOffset? ActivatedUtc { get; init; }
    public DateTimeOffset? RetiredUtc { get; init; }
    public string? Notes { get; init; }
    public long? ParentParameterSetId { get; init; }
    public string? ObjectiveFunctionVersion { get; init; }
    public string? CodeVersion { get; init; }
}
