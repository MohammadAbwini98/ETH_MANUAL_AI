using EthSignal.Domain.Models;

namespace EthSignal.Infrastructure.Apis;

public enum TickProviderKind { Rest, Playwright, Hybrid }

/// <summary>
/// Abstraction over how spot prices are delivered to the live tick processor.
/// Enables hot-swapping between REST polling and Playwright DOM scraping.
/// </summary>
public interface ITickProvider : IAsyncDisposable
{
    /// <summary>Kind of provider for logging/telemetry.</summary>
    TickProviderKind Kind { get; }

    /// <summary>True when the provider is delivering ticks without errors.</summary>
    bool IsHealthy { get; }

    /// <summary>
    /// Start the internal fetch loop for the given epic.
    /// Must be called before ReadAllAsync.
    /// </summary>
    Task StartAsync(string epic, CancellationToken ct);

    /// <summary>
    /// Infinite async sequence of spot prices.
    /// Never returns (runs until ct is cancelled).
    /// </summary>
    IAsyncEnumerable<SpotPrice> ReadAllAsync(CancellationToken ct);

    /// <summary>Approximate tick rate in Hz, measured over the last 10 seconds.</summary>
    double TickRateHz { get; }
}
