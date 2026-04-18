namespace EthSignal.Domain.Models;

public enum VolatilityTier { LOW, NORMAL, HIGH, EXTREME }
public enum TrendStrength { WEAK, MODERATE, STRONG }
public enum TradingSession { ASIA, LONDON, OVERLAP, NEW_YORK }
public enum SpreadQuality { TIGHT, NORMAL, WIDE }
public enum VolumeTier { DRY, NORMAL, ACTIVE }

/// <summary>
/// Composite market condition classification used by the adaptive parameter system.
/// Each dimension is independently classified from observable indicators.
/// </summary>
public sealed record MarketConditionClass(
    VolatilityTier Volatility,
    TrendStrength Trend,
    TradingSession Session,
    SpreadQuality Spread,
    VolumeTier Volume)
{
    public string ToKey() => $"{Volatility}_{Trend}_{Session}_{Spread}_{Volume}";

    /// <summary>
    /// Reduced-cardinality key for retrospective outcome aggregation.
    /// Volatility × Trend = 12 buckets — reachable in practice so the
    /// retrospective overlay system can actually fire.
    /// </summary>
    public string ToCoarseKey() => $"{Volatility}_{Trend}";

    public override string ToString() => ToKey();

    public static readonly MarketConditionClass Default = new(
        VolatilityTier.NORMAL, TrendStrength.MODERATE, TradingSession.LONDON,
        SpreadQuality.NORMAL, VolumeTier.NORMAL);
}
