namespace EthSignal.Domain.Models;

/// <summary>
/// Optional derivatives market context for ML feature extraction.
/// All values default to 0 (unavailable) when derivatives data is not ingested.
/// </summary>
public sealed record DerivativesContext
{
    /// <summary>Funding rate (e.g. 0.0001 = 0.01%).</summary>
    public decimal FundingRate { get; init; }

    /// <summary>Open interest in USD terms.</summary>
    public decimal OpenInterest { get; init; }

    /// <summary>Open interest change over recent period.</summary>
    public decimal OpenInterestChange { get; init; }

    /// <summary>Long/short ratio (e.g. 1.2 = more longs).</summary>
    public decimal LongShortRatio { get; init; }

    /// <summary>Basis between spot and perpetual (perp - spot) / spot.</summary>
    public decimal SpotPerpBasis { get; init; }

    /// <summary>Returns an empty context with all zeros (unavailable).</summary>
    public static DerivativesContext Unavailable { get; } = new();
}
