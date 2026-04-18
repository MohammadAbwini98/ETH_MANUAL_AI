using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Engine;

/// <summary>
/// B-07: Runtime parameter resolution.
/// All parameterized strategy services depend on this to get the active parameter set.
/// </summary>
public interface IParameterProvider
{
    /// <summary>Get the currently active parameters. Never returns null (falls back to defaults).</summary>
    StrategyParameters GetActive();

    /// <summary>Force a reload from the database. Returns true if successfully refreshed.</summary>
    Task<bool> RefreshAsync(CancellationToken ct = default);

    /// <summary>Force-override the MlMode in the cached parameters (e.g., to downgrade ACTIVE to SHADOW).</summary>
    void ForceOverrideMlMode(MlMode mode);
}
