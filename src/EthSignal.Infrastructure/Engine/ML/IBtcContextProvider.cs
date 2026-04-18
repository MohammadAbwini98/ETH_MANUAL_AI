using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Engine.ML;

/// <summary>
/// Provides BTC cross-asset context for ML feature extraction.
/// Implement this interface when BTC candle ingestion is available.
/// The default implementation returns Unavailable (all zeros).
/// </summary>
public interface IBtcContextProvider
{
    /// <summary>
    /// Get the current BTC context. Returns BtcCrossAssetContext.Unavailable
    /// when BTC data is not available.
    /// </summary>
    BtcCrossAssetContext GetCurrentContext();
}

/// <summary>
/// Default no-op provider that returns unavailable context.
/// Registered by default; replaced when BTC data ingestion is wired.
/// </summary>
public sealed class NullBtcContextProvider : IBtcContextProvider
{
    public BtcCrossAssetContext GetCurrentContext() => BtcCrossAssetContext.Unavailable;
}
