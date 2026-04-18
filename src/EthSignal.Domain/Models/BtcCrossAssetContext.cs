namespace EthSignal.Domain.Models;

/// <summary>
/// Optional BTC cross-asset context for ML feature extraction.
/// All values default to 0 (unavailable) when BTC data is not ingested.
/// </summary>
public sealed record BtcCrossAssetContext
{
    /// <summary>BTC close-to-close return over recent lookback (e.g. last 12 bars).</summary>
    public decimal BtcRecentReturn { get; init; }

    /// <summary>BTC regime label: 0=NEUTRAL, 1=BULLISH, 2=BEARISH.</summary>
    public int BtcRegimeLabel { get; init; }

    /// <summary>ETH return minus BTC return over the same lookback.</summary>
    public decimal EthBtcRelativeStrength { get; init; }

    /// <summary>Returns an empty context with all zeros (unavailable).</summary>
    public static BtcCrossAssetContext Unavailable { get; } = new();
}
