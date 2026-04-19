using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EthSignal.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EthSignal.Infrastructure.Apis;

public sealed class CapitalClient : ICapitalClient
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _identifier;
    private readonly string _password;
    private readonly ILogger<CapitalClient> _logger;

    private string? _cst;
    private string? _securityToken;
    private DateTimeOffset _lastAuthUtc;
    private static readonly TimeSpan TokenRefreshInterval = TimeSpan.FromHours(4);
    private readonly SemaphoreSlim _authLock = new(1, 1); // P8-03: protect concurrent auth

    public CapitalClient(string baseUrl, string apiKey, string identifier, string password,
        ILogger<CapitalClient>? logger = null)
    {
        _apiKey = apiKey;
        _identifier = identifier;
        _password = password;
        _logger = logger ?? NullLogger<CapitalClient>.Instance;
        _http = new HttpClient
        {
            BaseAddress = new Uri(NormalizeBaseUrl(baseUrl)),
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public async Task AuthenticateAsync(CancellationToken ct = default)
    {
        await _authLock.WaitAsync(ct);
        try
        {

            _logger.LogDebug("Sending auth request to {BaseUrl}", _http.BaseAddress);
            using var req = new HttpRequestMessage(HttpMethod.Post, "session");
            req.Headers.Add("X-CAP-API-KEY", _apiKey);
            req.Content = JsonContent.Create(new SessionRequest(_identifier, _password, false));

            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                var body = await res.Content.ReadAsStringAsync(ct);
                _logger.LogError("Authentication failed with status {Status}: {Body}", (int)res.StatusCode, body);
                throw new InvalidOperationException($"Authentication failed ({(int)res.StatusCode}): {body}");
            }

            if (!res.Headers.TryGetValues("CST", out var cstVals)
                || !res.Headers.TryGetValues("X-SECURITY-TOKEN", out var secVals))
                throw new InvalidOperationException("Missing session tokens in auth response.");

            _cst = cstVals.First();
            _securityToken = secVals.First();
            _lastAuthUtc = DateTimeOffset.UtcNow;
            _logger.LogInformation("Authenticated successfully with Capital.com");
        }
        finally
        {
            _authLock.Release();
        }
    }

    public async Task<SpotPrice> GetSpotPriceAsync(string epic, CancellationToken ct = default)
    {
        var payload = await SendAuthorizedGetAsync($"markets/{Uri.EscapeDataString(epic)}", ct);
        var data = JsonSerializer.Deserialize<MarketDetailsResponse>(payload, JsonOpts);

        var bid = data?.Snapshot?.Bid ?? data?.Bid ?? 0m;
        var ask = data?.Snapshot?.Offer ?? data?.Offer ?? 0m;
        var mid = (bid + ask) / 2m;

        var timestamp = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(data?.Snapshot?.UpdateTimeUtc)
            && DateTimeOffset.TryParse(data.Snapshot.UpdateTimeUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            timestamp = parsed;
        }

        return new SpotPrice(bid, ask, mid, timestamp);
    }

    /// <summary>
    /// Fetches candles preserving separate bid and ask OHLC.
    /// </summary>
    public async Task<List<RichCandle>> GetCandlesAsync(
        string epic, string resolution, DateTimeOffset fromUtc, DateTimeOffset toUtc, int max,
        CancellationToken ct = default)
    {
        var escapedEpic = Uri.EscapeDataString(epic);
        var escapedRes = Uri.EscapeDataString(resolution);
        var from = Uri.EscapeDataString(fromUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
        var to = Uri.EscapeDataString(toUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));

        var path = $"prices/{escapedEpic}?resolution={escapedRes}&from={from}&to={to}&max={Math.Min(max, 1000)}";
        var payload = await SendAuthorizedGetAsync(path, ct);
        var response = JsonSerializer.Deserialize<PricesResponse>(payload, JsonOpts);

        var received = DateTimeOffset.UtcNow;
        var candles = new List<RichCandle>();

        foreach (var row in response?.Prices ?? [])
        {
            if (string.IsNullOrWhiteSpace(row.SnapshotTimeUtc))
                continue;

            if (!DateTimeOffset.TryParse(row.SnapshotTimeUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var openTime))
                continue;

            candles.Add(new RichCandle
            {
                OpenTime = openTime,
                BidOpen = row.OpenPrice?.Bid ?? 0m,
                BidHigh = row.HighPrice?.Bid ?? 0m,
                BidLow = row.LowPrice?.Bid ?? 0m,
                BidClose = row.ClosePrice?.Bid ?? 0m,
                AskOpen = row.OpenPrice?.Ask ?? 0m,
                AskHigh = row.HighPrice?.Ask ?? 0m,
                AskLow = row.LowPrice?.Ask ?? 0m,
                AskClose = row.ClosePrice?.Ask ?? 0m,
                Volume = row.LastTradedVolume ?? 0m,
                SourceTimestampUtc = openTime,
                ReceivedTimestampUtc = received
            });
        }

        candles.Sort((a, b) => a.OpenTime.CompareTo(b.OpenTime));
        return candles;
    }

    private Sentiment _lastValidSentiment = new(0m, 0m);

    public async Task<Sentiment> GetSentimentAsync(string marketId, CancellationToken ct = default)
    {
        try
        {
            var payload = await SendAuthorizedGetAsync(
                $"clientsentiment/{Uri.EscapeDataString(marketId)}", ct);
            var data = JsonSerializer.Deserialize<SentimentResponse>(payload, JsonOpts);
            var sentiment = new Sentiment(
                data?.LongPositionPercentage ?? 0m,
                data?.ShortPositionPercentage ?? 0m);

            if (sentiment.BuyerPct > 0 || sentiment.SellerPct > 0)
                _lastValidSentiment = sentiment;

            return sentiment;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sentiment fetch failed for {MarketId}, returning last valid value", marketId);
            return _lastValidSentiment;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!string.IsNullOrWhiteSpace(_cst))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Delete, "session");
                req.Headers.Add("X-CAP-API-KEY", _apiKey);
                req.Headers.Add("CST", _cst);
                req.Headers.Add("X-SECURITY-TOKEN", _securityToken);
                await _http.SendAsync(req);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[CapitalClient] Session logout failed; session may persist server-side");
            }
        }
        _http.Dispose();
    }

    private async Task<string> SendAuthorizedGetAsync(string path, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            // CF-05: Proactive token refresh before expiry
            if (string.IsNullOrWhiteSpace(_cst)
                || (DateTimeOffset.UtcNow - _lastAuthUtc) > TokenRefreshInterval)
            {
                _cst = null;
                _securityToken = null;
                await AuthenticateAsync(ct);
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, path);
            req.Headers.Add("X-CAP-API-KEY", _apiKey);
            req.Headers.Add("CST", _cst);
            req.Headers.Add("X-SECURITY-TOKEN", _securityToken);

            using var res = await _http.SendAsync(req, ct);
            var payload = await res.Content.ReadAsStringAsync(ct);

            if (res.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                _cst = null;
                _securityToken = null;
                continue;
            }

            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"Request failed ({(int)res.StatusCode}) for '{path}': {payload}");

            return payload;
        }

        throw new InvalidOperationException($"Request failed after retry for '{path}'.");
    }

    private static string NormalizeBaseUrl(string url)
    {
        var trimmed = url.Trim().TrimEnd('/');
        if (!trimmed.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
            trimmed += "/api/v1";
        return trimmed + "/";
    }

    // --- DTOs ---

    private record SessionRequest(
        [property: JsonPropertyName("identifier")] string Identifier,
        [property: JsonPropertyName("password")] string Password,
        [property: JsonPropertyName("encryptedPassword")] bool EncryptedPassword);

    private class PricesResponse
    {
        [JsonPropertyName("prices")] public List<PriceRow> Prices { get; init; } = [];
    }

    private class PriceRow
    {
        [JsonPropertyName("snapshotTimeUTC")] public string? SnapshotTimeUtc { get; init; }
        [JsonPropertyName("openPrice")] public PriceLevel? OpenPrice { get; init; }
        [JsonPropertyName("closePrice")] public PriceLevel? ClosePrice { get; init; }
        [JsonPropertyName("highPrice")] public PriceLevel? HighPrice { get; init; }
        [JsonPropertyName("lowPrice")] public PriceLevel? LowPrice { get; init; }
        [JsonPropertyName("lastTradedVolume")] public decimal? LastTradedVolume { get; init; }
    }

    internal class PriceLevel
    {
        [JsonPropertyName("bid")] public decimal? Bid { get; init; }
        [JsonPropertyName("ask")] public decimal? Ask { get; init; }
    }

    private class MarketDetailsResponse
    {
        [JsonPropertyName("bid")] public decimal? Bid { get; init; }
        [JsonPropertyName("offer")] public decimal? Offer { get; init; }
        [JsonPropertyName("snapshot")] public SnapshotRow? Snapshot { get; init; }
    }

    private class SnapshotRow
    {
        [JsonPropertyName("bid")] public decimal? Bid { get; init; }
        [JsonPropertyName("offer")] public decimal? Offer { get; init; }
        [JsonPropertyName("updateTimeUTC")] public string? UpdateTimeUtc { get; init; }
    }

    private class SentimentResponse
    {
        [JsonPropertyName("longPositionPercentage")] public decimal? LongPositionPercentage { get; init; }
        [JsonPropertyName("shortPositionPercentage")] public decimal? ShortPositionPercentage { get; init; }
    }
}
