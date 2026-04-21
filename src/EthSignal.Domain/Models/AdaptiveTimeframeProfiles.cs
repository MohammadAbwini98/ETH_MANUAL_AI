namespace EthSignal.Domain.Models;

/// <summary>
/// Latest persisted adaptive runtime state for one symbol/timeframe pair.
/// Represents the currently active dynamic setup after base timeframe-profile
/// resolution plus adaptive market overlays.
/// </summary>
public sealed record AdaptiveTimeframeProfileState
{
    public required string Symbol { get; init; }
    public required string Timeframe { get; init; }
    public required string StrategyVersion { get; init; }
    public required TimeframeProfileBucket ProfileBucket { get; init; }
    public bool AdaptiveEnabled { get; init; }
    public bool RetrospectiveEnabled { get; init; }
    public bool HasRetrospectiveOverlay { get; init; }
    public decimal EffectiveIntensity { get; init; }
    public string? CurrentConditionClass { get; init; }
    public string? CurrentCoarseConditionKey { get; init; }
    public string? OverlayDiffsJson { get; init; }
    public ParameterOverlay? RetrospectiveOverlay { get; init; }
    public required StrategyParameters BaseParameters { get; init; }
    public required StrategyParameters EffectiveParameters { get; init; }
    public required string BaseParameterHash { get; init; }
    public required string EffectiveParameterHash { get; init; }
    public DateTimeOffset LastEvaluatedBarUtc { get; init; }
    public DateTimeOffset LastChangedUtc { get; init; }
    public long ChangeVersion { get; init; }
}

/// <summary>
/// Append-only audit trail of adaptive setup changes for one symbol/timeframe pair.
/// A new row is stored only when the active setup meaningfully changes.
/// </summary>
public sealed record AdaptiveTimeframeProfileChange
{
    public long Id { get; init; }
    public required string Symbol { get; init; }
    public required string Timeframe { get; init; }
    public required string StrategyVersion { get; init; }
    public required TimeframeProfileBucket ProfileBucket { get; init; }
    public required string ChangeReason { get; init; }
    public string? PreviousConditionClass { get; init; }
    public string? CurrentConditionClass { get; init; }
    public string? PreviousParameterHash { get; init; }
    public required string CurrentParameterHash { get; init; }
    public bool AdaptiveEnabled { get; init; }
    public bool RetrospectiveEnabled { get; init; }
    public bool HasRetrospectiveOverlay { get; init; }
    public decimal EffectiveIntensity { get; init; }
    public string? OverlayDiffsJson { get; init; }
    public ParameterOverlay? RetrospectiveOverlay { get; init; }
    public required StrategyParameters BaseParameters { get; init; }
    public required StrategyParameters EffectiveParameters { get; init; }
    public DateTimeOffset BarTimeUtc { get; init; }
    public DateTimeOffset ChangedAtUtc { get; init; }
    public long ChangeVersion { get; init; }
}
