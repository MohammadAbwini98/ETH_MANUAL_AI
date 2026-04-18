using System.Runtime.CompilerServices;
using EthSignal.Domain.Models;
using Microsoft.Extensions.Logging;

namespace EthSignal.Infrastructure.Apis;

public sealed class RestTickProvider : ITickProvider
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly ICapitalClient _api;
    private readonly ILogger<RestTickProvider> _logger;
    private string _epic = "";
    private DateTimeOffset _nextPollAtUtc;

    public TickProviderKind Kind      => TickProviderKind.Rest;
    public bool             IsHealthy => true;
    public double           TickRateHz => 1.0;

    public RestTickProvider(ICapitalClient api, ILogger<RestTickProvider> logger)
    {
        _api    = api;
        _logger = logger;
    }

    public Task StartAsync(string epic, CancellationToken ct)
    {
        _epic = epic;
        _nextPollAtUtc = DateTimeOffset.UtcNow.Add(PollInterval);
        _logger.LogInformation("[REST] Tick provider started for {Epic}", epic);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<SpotPrice> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var scheduledPollAt = _nextPollAtUtc == default
                ? DateTimeOffset.UtcNow.Add(PollInterval)
                : _nextPollAtUtc;
            var wait = scheduledPollAt - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(wait, ct);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
            }

            SpotPrice? spot = null;

            try { spot = await _api.GetSpotPriceAsync(_epic, ct); }
            catch (OperationCanceledException) { yield break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[REST] GetSpotPrice failed");
            }

            if (spot != null) yield return spot;

            _nextPollAtUtc = scheduledPollAt.Add(PollInterval);
            if (_nextPollAtUtc <= DateTimeOffset.UtcNow)
                _nextPollAtUtc = DateTimeOffset.UtcNow.Add(PollInterval);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
