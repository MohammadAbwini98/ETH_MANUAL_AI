using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Engine.ML;

/// <summary>
/// Provides derivatives market context (funding rate, open interest, etc.)
/// for ML feature extraction.
/// Implement this interface when derivatives data ingestion is available.
/// The default implementation returns Unavailable (all zeros).
/// </summary>
public interface IDerivativesContextProvider
{
    /// <summary>
    /// Get the current derivatives context. Returns DerivativesContext.Unavailable
    /// when derivatives data is not available.
    /// </summary>
    DerivativesContext GetCurrentContext();
}

/// <summary>
/// Default no-op provider that returns unavailable context.
/// Registered by default; replaced when derivatives data ingestion is wired.
/// </summary>
public sealed class NullDerivativesContextProvider : IDerivativesContextProvider
{
    public DerivativesContext GetCurrentContext() => DerivativesContext.Unavailable;
}
