namespace EthSignal.Domain.Models;

/// <summary>
/// Phase 5: Risk management policy.
/// Prefer StrategyParameters.ToRiskPolicy() to construct this — all live signal
/// paths use that method. These defaults are aligned with StrategyParameters
/// defaults to prevent silent divergence when RiskPolicy is created directly.
/// </summary>
public sealed record RiskPolicy
{
    public decimal AccountBalanceUsd { get; init; } = 50m;
    public decimal RiskPercentPerTrade { get; init; } = 0.5m;
    public decimal HardMaxRiskPercent { get; init; } = 1.0m;
    // Aligned with StrategyParameters.DailyLossCapPercent (was 2.0m, corrected to 5.0m)
    public decimal DailyMaxDrawdownPercent { get; init; } = decimal.MaxValue;
    // Aligned with StrategyParameters.MaxConsecutiveLossesPerDay (unlimited by default)
    public int MaxConsecutiveLossesPerDay { get; init; } = int.MaxValue;
    // Aligned with StrategyParameters.MaxOpenPositions (unlimited)
    public int MaxOpenPositions { get; init; } = int.MaxValue;
    // Aligned with StrategyParameters.StopAtrMultiplier.
    public decimal AtrMultiplier { get; init; } = 2.0m;
    // Aligned with StrategyParameters.TargetRMultiple.
    public decimal RewardToRisk { get; init; } = 1.5m;
    // Aligned with StrategyParameters.MinAtrThreshold (was 0.5m, corrected to 0.8m)
    public decimal MinAtrThreshold { get; init; } = 0.8m;
    // Aligned with StrategyParameters.MaxSpreadPct (was 0.003m, corrected to 0.004m)
    public decimal MaxSpreadPct { get; init; } = 0.004m;
    public decimal MinRiskRewardAfterRounding { get; init; } = 1.5m;

    // Scalp-specific session limits (0 = inherit shared limit above)
    public int ScalpMaxConsecutiveLossesPerDay { get; init; } = int.MaxValue;
    public decimal ScalpDailyMaxDrawdownPercent { get; init; } = 3.0m;
}
