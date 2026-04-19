namespace EthSignal.Infrastructure.Db;

public interface IPortalOverridesRepository
{
    Task EnsureTableExistsAsync(CancellationToken ct = default);
    Task<PortalOverrides?> GetAsync(CancellationToken ct = default);
    Task SaveAsync(PortalOverrides overrides, CancellationToken ct = default);
}

/// <summary>
/// Portal-managed runtime overrides for signal blocker parameters and global execution flags.
/// Null fields mean "not overridden" — the active parameter set value is used.
/// Stored in ETH.portal_overrides and applied on top of strategy_parameter_sets.
/// </summary>
public sealed record PortalOverrides
{
    public int? MaxOpenPositions { get; init; }
    public int? MaxOpenPerTimeframe { get; init; }
    public int? MaxOpenPerDirection { get; init; }
    public decimal? DailyLossCapPercent { get; init; }
    public int? MaxConsecutiveLossesPerDay { get; init; }
    public int? ScalpMaxConsecutiveLossesPerDay { get; init; }
    public bool? RecommendedSignalExecutionEnabled { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public string? UpdatedBy { get; init; }
}
