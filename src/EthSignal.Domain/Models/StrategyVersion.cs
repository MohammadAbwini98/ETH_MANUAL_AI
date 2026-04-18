namespace EthSignal.Domain.Models;

/// <summary>Phase 7: Strategy version with configurable weights.</summary>
public sealed record StrategyVersion
{
    public required string Version { get; init; }
    public bool IsDraft { get; init; } = true;
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    // Score weights (must sum to 100)
    public int WeightRegime { get; init; } = 20;
    public int WeightEma { get; init; } = 15;
    public int WeightRsi { get; init; } = 15;
    public int WeightMacd { get; init; } = 15;
    public int WeightAdx { get; init; } = 10;
    public int WeightVwap { get; init; } = 10;
    public int WeightVolume { get; init; } = 10;
    public int WeightSpread { get; init; } = 5;

    public int ActionableThreshold { get; init; } = 70;
}
