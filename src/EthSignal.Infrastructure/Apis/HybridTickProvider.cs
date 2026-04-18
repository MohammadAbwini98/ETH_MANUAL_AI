using System.Runtime.CompilerServices;
using System.Threading.Channels;
using EthSignal.Domain.Models;
using Microsoft.Extensions.Logging;

namespace EthSignal.Infrastructure.Apis;

/// <summary>
/// Primary: PlaywrightTickProvider (200 ms / ~5 Hz).
/// Fallback: RestTickProvider (1 sec / ~1 Hz).
/// Automatically switches when Playwright is unhealthy for more than FallbackThresholdSec.
/// </summary>
public sealed class HybridTickProvider : ITickProvider
{
    private const int FallbackThresholdSec = 15;
    private const int RestPollMs           = 1_000;

    private readonly PlaywrightTickProvider _playwright;
    private readonly ICapitalClient         _api;
    private readonly ILogger<HybridTickProvider> _logger;

    private readonly Channel<SpotPrice> _merged =
        Channel.CreateBounded<SpotPrice>(new BoundedChannelOptions(500)
        {
            FullMode     = BoundedChannelFullMode.DropOldest,
            SingleWriter = false,
            SingleReader = false
        });

    private bool   _usingFallback    = false;
    private string _currentEpic      = "";

    public TickProviderKind Kind      => TickProviderKind.Hybrid;
    public bool             IsHealthy => _playwright.IsHealthy || _usingFallback;
    public double TickRateHz =>
        _usingFallback ? 1.0 : _playwright.TickRateHz;

    public HybridTickProvider(
        PlaywrightTickProvider playwright,
        ICapitalClient api,
        ILogger<HybridTickProvider> logger)
    {
        _playwright = playwright;
        _api        = api;
        _logger     = logger;
    }

    public Task StartAsync(string epic, CancellationToken ct)
    {
        _currentEpic = epic;
        _usingFallback = true; // Start with REST immediately while Playwright starts up

        _logger.LogInformation("[Hybrid] Starting with REST — Playwright launching in background");

        // Start Playwright in background (waits for user login, then switches)
        _ = Task.Run(async () =>
        {
            try
            {
                await _playwright.StartAsync(epic, ct);
                _logger.LogInformation("[Hybrid] Playwright started — will switch from REST when healthy");
                // Start forwarding Playwright ticks to merged channel
                _ = Task.Run(() => ForwardPlaywrightAsync(ct), ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Hybrid] Playwright failed to start");
            }
        }, ct);

        // REST fallback loop (always running; only writes when _usingFallback=true)
        _ = Task.Run(() => RestFallbackLoopAsync(ct), ct);

        // Health watchdog (detects when Playwright becomes healthy and switches)
        _ = Task.Run(() => HealthWatchdogAsync(ct), ct);

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<SpotPrice> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var price in _merged.Reader.ReadAllAsync(ct))
            yield return price;
    }

    private async Task ForwardPlaywrightAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var spot in _playwright.ReadAllAsync(ct))
            {
                if (!_usingFallback)
                    _merged.Writer.TryWrite(spot);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Hybrid] Playwright forwarder crashed");
        }
    }

    private async Task RestFallbackLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_usingFallback)
            {
                try
                {
                    var spot = await _api.GetSpotPriceAsync(_currentEpic, ct);
                    _merged.Writer.TryWrite(spot);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Hybrid] REST fallback fetch failed");
                }
            }
            await Task.Delay(RestPollMs, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task HealthWatchdogAsync(CancellationToken ct)
    {
        var unhealthySince = (DateTimeOffset?)null;

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(2_000, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            var playwrightOk = _playwright.IsHealthy;

            if (!playwrightOk && !_usingFallback)
            {
                unhealthySince ??= DateTimeOffset.UtcNow;
                var seconds = (DateTimeOffset.UtcNow - unhealthySince.Value).TotalSeconds;

                if (seconds >= FallbackThresholdSec)
                {
                    _usingFallback = true;
                    _logger.LogWarning(
                        "[Hybrid] Playwright unhealthy for {Sec:F0}s — switching to REST fallback",
                        seconds);
                }
            }
            else if (playwrightOk && _usingFallback)
            {
                _usingFallback = false;
                unhealthySince = null;
                _logger.LogInformation("[Hybrid] Playwright recovered — resuming high-frequency ticks");
            }
            else if (playwrightOk)
            {
                unhealthySince = null;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _playwright.DisposeAsync();
        _merged.Writer.TryComplete();
    }
}
