using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Trading;

namespace EthSignal.Infrastructure.Apis;

public interface ICapitalClient : ICapitalTradingClient, IAsyncDisposable
{
    Task AuthenticateAsync(CancellationToken ct = default);
    Task<SpotPrice> GetSpotPriceAsync(string epic, CancellationToken ct = default);
    Task<List<RichCandle>> GetCandlesAsync(string epic, string resolution, DateTimeOffset fromUtc, DateTimeOffset toUtc, int max, CancellationToken ct = default);
    Task<Sentiment> GetSentimentAsync(string marketId, CancellationToken ct = default);
}
