using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Trading;
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
    private readonly string _preferredDemoAccountName;
    private readonly ILogger<CapitalClient> _logger;

    private string? _cst;
    private string? _securityToken;
    private string? _activeAccountId;
    private string? _activeAccountName;
    private DateTimeOffset _lastAuthUtc;
    private static readonly TimeSpan TokenRefreshInterval = TimeSpan.FromHours(4);
    private readonly SemaphoreSlim _authLock = new(1, 1); // P8-03: protect concurrent auth
    private readonly SemaphoreSlim _accountSelectionLock = new(1, 1);
    public bool IsDemoEnvironment { get; }

    public CapitalClient(string baseUrl, string apiKey, string identifier, string password,
        string? preferredDemoAccountName = null,
        ILogger<CapitalClient>? logger = null,
        HttpClient? httpClient = null)
    {
        _apiKey = apiKey;
        _identifier = identifier;
        _password = password;
        _preferredDemoAccountName = string.IsNullOrWhiteSpace(preferredDemoAccountName)
            ? "DEMOAI"
            : preferredDemoAccountName.Trim();
        _logger = logger ?? NullLogger<CapitalClient>.Instance;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.BaseAddress = new Uri(NormalizeBaseUrl(baseUrl));
        IsDemoEnvironment = _http.BaseAddress.Host.Contains("demo-api-capital.", StringComparison.OrdinalIgnoreCase);
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

    public async Task EnsureDemoReadyAsync(CancellationToken ct = default)
    {
        if (!IsDemoEnvironment)
            throw new InvalidOperationException("Capital trading is allowed only against the demo API base URL.");

        await EnsurePreferredDemoAccountSelectedAsync(ct);
        var account = await GetAccountInfoAsync(ct);
        if (!account.IsDemo)
            throw new InvalidOperationException("Capital trading guard rejected the active account because it is not demo.");
        if (!string.Equals(account.AccountName, _preferredDemoAccountName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Capital trading guard rejected the active demo account because '{account.AccountName}' is active instead of the required '{_preferredDemoAccountName}'.");
        }
    }

    public async Task<CapitalMarketInfo> GetMarketInfoAsync(string epic, CancellationToken ct = default)
    {
        var payload = await SendAuthorizedGetAsync($"markets/{Uri.EscapeDataString(epic)}", ct);
        var data = JsonSerializer.Deserialize<MarketEnvelope>(payload, JsonOpts)
            ?? throw new InvalidOperationException($"Capital market details for {epic} returned an empty payload.");

        return new CapitalMarketInfo
        {
            Epic = data.Instrument?.Epic ?? epic,
            Symbol = data.Instrument?.Symbol ?? epic,
            InstrumentName = data.Instrument?.Name ?? data.Instrument?.Symbol ?? epic,
            Currency = data.Instrument?.Currency ?? "USD",
            Tradeable = string.Equals(data.Snapshot?.MarketStatus, "TRADEABLE", StringComparison.OrdinalIgnoreCase),
            Bid = data.Snapshot?.Bid ?? data.Bid ?? 0m,
            Offer = data.Snapshot?.Offer ?? data.Offer ?? 0m,
            DecimalPlaces = data.Snapshot?.DecimalPlacesFactor ?? 2,
            MinDealSize = data.DealingRules?.MinDealSize?.Value ?? 1m,
            MinSizeIncrement = data.DealingRules?.MinSizeIncrement?.Value ?? 1m,
            MinStopOrProfitDistance = data.DealingRules?.MinStopOrProfitDistance?.Value ?? 0m,
            MinStopOrProfitDistanceUnit = data.DealingRules?.MinStopOrProfitDistance?.Unit ?? "",
            MarginFactor = data.Instrument?.MarginFactor ?? 0m,
            MarginFactorUnit = data.Instrument?.MarginFactorUnit ?? ""
        };
    }

    public async Task<CapitalAccountInfo> GetAccountInfoAsync(CancellationToken ct = default)
    {
        await EnsurePreferredDemoAccountSelectedAsync(ct);
        var accountsPayload = await SendAuthorizedGetAsync("accounts", ct);
        var prefsPayload = await SendAuthorizedGetAsync("accounts/preferences", ct);
        var accounts = JsonSerializer.Deserialize<AccountsResponse>(accountsPayload, JsonOpts)
            ?? throw new InvalidOperationException("Capital accounts endpoint returned an empty payload.");
        var prefs = JsonSerializer.Deserialize<AccountPreferencesResponse>(prefsPayload, JsonOpts);

        var account = accounts.Accounts?
            .OrderByDescending(a => a.Preferred)
            .FirstOrDefault(a => string.Equals(a.Status, "ENABLED", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("No enabled Capital account is available for execution.");

        _activeAccountId = account.AccountId;
        _activeAccountName = account.AccountName;
        var balance = account.Balance?.Balance ?? 0m;
        var profitLoss = account.Balance?.ProfitLoss ?? 0m;
        return new CapitalAccountInfo
        {
            AccountId = account.AccountId ?? "",
            AccountName = account.AccountName ?? "",
            Currency = account.Currency ?? "USD",
            Balance = balance,
            Available = account.Balance?.Available ?? 0m,
            ProfitLoss = profitLoss,
            Equity = balance + profitLoss,
            HedgingMode = prefs?.HedgingMode ?? false,
            IsDemo = IsDemoEnvironment
        };
    }

    public async Task<CapitalOpenPositionResult> PlacePositionAsync(CapitalPlacePositionRequest request, CancellationToken ct = default)
    {
        await EnsurePreferredDemoAccountSelectedAsync(ct);
        var body = new
        {
            epic = request.Epic,
            direction = request.Direction.ToString(),
            size = request.Size,
            guaranteedStop = false,
            trailingStop = false,
            stopLevel = request.StopLevel,
            profitLevel = request.ProfitLevel
        };

        var payload = await SendAuthorizedJsonAsync(HttpMethod.Post, "positions", body, ct);
        var result = JsonSerializer.Deserialize<DealReferenceResponse>(payload, JsonOpts)
            ?? throw new InvalidOperationException("Capital position create response was empty.");
        if (string.IsNullOrWhiteSpace(result.DealReference))
            throw new InvalidOperationException($"Capital position create response did not include a dealReference. Payload: {payload}");

        return new CapitalOpenPositionResult
        {
            DealReference = result.DealReference,
            Note = payload
        };
    }

    public async Task<CapitalDealConfirmation> ConfirmDealAsync(string dealReference, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                var payload = await SendAuthorizedGetAsync($"confirms/{Uri.EscapeDataString(dealReference)}", ct);
                var data = JsonSerializer.Deserialize<ConfirmDealResponse>(payload, JsonOpts)
                    ?? throw new InvalidOperationException("Capital confirm response was empty.");
                var direction = Enum.TryParse<SignalDirection>(data.Direction ?? "", true, out var parsedDirection)
                    ? parsedDirection
                    : (SignalDirection?)null;
                var accepted = string.Equals(data.DealStatus, "ACCEPTED", StringComparison.OrdinalIgnoreCase);
                return new CapitalDealConfirmation
                {
                    DealReference = data.DealReference ?? dealReference,
                    DealId = data.DealId ?? data.AffectedDeals?.FirstOrDefault()?.DealId,
                    Status = data.Status,
                    DealStatus = data.DealStatus,
                    Epic = data.Epic,
                    Level = data.Level,
                    Size = data.Size,
                    Direction = direction,
                    Accepted = accepted,
                    RejectionReason = accepted ? null : data.Reason ?? data.Status ?? data.DealStatus,
                    Note = payload
                };
            }
            catch (InvalidOperationException ex) when (attempt < 3 && ex.Message.Contains("(404)"))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400 * (attempt + 1)), ct);
            }
        }

        throw new InvalidOperationException($"Capital confirmation for deal reference {dealReference} did not become available in time.");
    }

    public async Task<IReadOnlyList<CapitalPositionSnapshot>> GetOpenPositionsAsync(CancellationToken ct = default)
    {
        await EnsurePreferredDemoAccountSelectedAsync(ct);
        var payload = await SendAuthorizedGetAsync("positions", ct);
        var data = JsonSerializer.Deserialize<OpenPositionsResponse>(payload, JsonOpts) ?? new OpenPositionsResponse();
        return data.Positions
            .Select(p => new CapitalPositionSnapshot
            {
                DealId = p.Position?.DealId ?? "",
                Epic = p.Market?.Epic ?? p.Position?.Epic ?? "",
                Direction = Enum.TryParse<SignalDirection>(p.Position?.Direction ?? "", true, out var dir)
                    ? dir
                    : SignalDirection.NO_TRADE,
                Size = p.Position?.Size ?? 0m,
                Level = p.Position?.Level ?? 0m,
                StopLevel = p.Position?.StopLevel,
                ProfitLevel = p.Position?.ProfitLevel,
                Currency = p.Market?.Currency ?? "USD"
            })
            .Where(p => !string.IsNullOrWhiteSpace(p.DealId))
            .ToList();
    }

    public async Task<CapitalClosePositionResult> ClosePositionAsync(CapitalClosePositionRequest request, CancellationToken ct = default)
    {
        await EnsurePreferredDemoAccountSelectedAsync(ct);
        var payload = await SendAuthorizedAsync(HttpMethod.Delete, $"positions/{Uri.EscapeDataString(request.DealId)}", null, ct);
        var result = JsonSerializer.Deserialize<DealReferenceResponse>(payload, JsonOpts)
            ?? throw new InvalidOperationException("Capital close position response was empty.");
        if (string.IsNullOrWhiteSpace(result.DealReference))
            throw new InvalidOperationException($"Capital close response did not include a dealReference. Payload: {payload}");
        return new CapitalClosePositionResult
        {
            DealReference = result.DealReference,
            Note = payload
        };
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

    private async Task EnsurePreferredDemoAccountSelectedAsync(CancellationToken ct)
    {
        if (!IsDemoEnvironment)
            return;

        await _accountSelectionLock.WaitAsync(ct);
        try
        {
            await AuthenticateAsync(ct);
            var accounts = await GetAccountsAsync(ct);
            var target = accounts
                .Where(a => string.Equals(a.Status, "ENABLED", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(a => string.Equals(a.AccountName, _preferredDemoAccountName, StringComparison.OrdinalIgnoreCase));

            if (target is null || string.IsNullOrWhiteSpace(target.AccountId))
            {
                throw new InvalidOperationException(
                    $"Preferred Capital demo account '{_preferredDemoAccountName}' was not found among enabled accounts.");
            }

            if (target.Preferred)
            {
                _activeAccountId = target.AccountId;
                _activeAccountName = target.AccountName;
                return;
            }

            _logger.LogInformation(
                "Switching Capital active account to preferred demo account {AccountName} ({AccountId})",
                target.AccountName, target.AccountId);

            await SendAuthorizedJsonAsync(HttpMethod.Put, "session", new { accountId = target.AccountId }, ct);

            var refreshedAccounts = await GetAccountsAsync(ct);
            var active = refreshedAccounts
                .Where(a => string.Equals(a.Status, "ENABLED", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(a => a.Preferred)
                .FirstOrDefault();

            if (active is null
                || !active.Preferred
                || !string.Equals(active.AccountId, target.AccountId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Capital account switch did not activate the required demo account '{_preferredDemoAccountName}'.");
            }

            _activeAccountId = active.AccountId;
            _activeAccountName = active.AccountName;
            _logger.LogInformation(
                "Capital active account confirmed as demo account {AccountName} ({AccountId})",
                active.AccountName, active.AccountId);
        }
        finally
        {
            _accountSelectionLock.Release();
        }
    }

    private async Task<List<AccountRow>> GetAccountsAsync(CancellationToken ct)
    {
        var accountsPayload = await SendAuthorizedGetAsync("accounts", ct);
        var accounts = JsonSerializer.Deserialize<AccountsResponse>(accountsPayload, JsonOpts)
            ?? throw new InvalidOperationException("Capital accounts endpoint returned an empty payload.");
        return accounts.Accounts ?? [];
    }

    private async Task<string> SendAuthorizedGetAsync(string path, CancellationToken ct)
        => await SendAuthorizedAsync(HttpMethod.Get, path, null, ct);

    private async Task<string> SendAuthorizedJsonAsync(HttpMethod method, string path, object body, CancellationToken ct)
        => await SendAuthorizedAsync(method, path, () => JsonContent.Create(body), ct);

    private async Task<string> SendAuthorizedAsync(HttpMethod method, string path, CancellationToken ct)
        => await SendAuthorizedAsync(method, path, contentFactory: null, ct);

    private async Task<string> SendAuthorizedAsync(HttpMethod method, string path, Func<HttpContent?>? contentFactory, CancellationToken ct)
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

            using var req = new HttpRequestMessage(method, path);
            req.Headers.Add("X-CAP-API-KEY", _apiKey);
            req.Headers.Add("CST", _cst);
            req.Headers.Add("X-SECURITY-TOKEN", _securityToken);
            req.Content = contentFactory?.Invoke();

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

    private class MarketEnvelope : MarketDetailsResponse
    {
        [JsonPropertyName("instrument")] public InstrumentRow? Instrument { get; init; }
        [JsonPropertyName("dealingRules")] public DealingRulesRow? DealingRules { get; init; }
    }

    private class SnapshotRow
    {
        [JsonPropertyName("bid")] public decimal? Bid { get; init; }
        [JsonPropertyName("offer")] public decimal? Offer { get; init; }
        [JsonPropertyName("updateTimeUTC")] public string? UpdateTimeUtc { get; init; }
        [JsonPropertyName("marketStatus")] public string? MarketStatus { get; init; }
        [JsonPropertyName("decimalPlacesFactor")] public int? DecimalPlacesFactor { get; init; }
    }

    private class SentimentResponse
    {
        [JsonPropertyName("longPositionPercentage")] public decimal? LongPositionPercentage { get; init; }
        [JsonPropertyName("shortPositionPercentage")] public decimal? ShortPositionPercentage { get; init; }
    }

    private class InstrumentRow
    {
        [JsonPropertyName("epic")] public string? Epic { get; init; }
        [JsonPropertyName("symbol")] public string? Symbol { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("currency")] public string? Currency { get; init; }
        [JsonPropertyName("marginFactor")] public decimal? MarginFactor { get; init; }
        [JsonPropertyName("marginFactorUnit")] public string? MarginFactorUnit { get; init; }
    }

    private class DealingRulesRow
    {
        [JsonPropertyName("minDealSize")] public RuleValueRow? MinDealSize { get; init; }
        [JsonPropertyName("minSizeIncrement")] public RuleValueRow? MinSizeIncrement { get; init; }
        [JsonPropertyName("minStopOrProfitDistance")] public RuleValueRow? MinStopOrProfitDistance { get; init; }
    }

    private class RuleValueRow
    {
        [JsonPropertyName("unit")] public string? Unit { get; init; }
        [JsonPropertyName("value")] public decimal? Value { get; init; }
    }

    private class AccountsResponse
    {
        [JsonPropertyName("accounts")] public List<AccountRow>? Accounts { get; init; }
    }

    private class AccountRow
    {
        [JsonPropertyName("accountId")] public string? AccountId { get; init; }
        [JsonPropertyName("accountName")] public string? AccountName { get; init; }
        [JsonPropertyName("status")] public string? Status { get; init; }
        [JsonPropertyName("preferred")] public bool Preferred { get; init; }
        [JsonPropertyName("currency")] public string? Currency { get; init; }
        [JsonPropertyName("balance")] public AccountBalanceRow? Balance { get; init; }
    }

    private class AccountBalanceRow
    {
        [JsonPropertyName("balance")] public decimal? Balance { get; init; }
        [JsonPropertyName("available")] public decimal? Available { get; init; }
        [JsonPropertyName("profitLoss")] public decimal? ProfitLoss { get; init; }
    }

    private class AccountPreferencesResponse
    {
        [JsonPropertyName("hedgingMode")] public bool HedgingMode { get; init; }
    }

    private class DealReferenceResponse
    {
        [JsonPropertyName("dealReference")] public string? DealReference { get; init; }
    }

    private class ConfirmDealResponse
    {
        [JsonPropertyName("status")] public string? Status { get; init; }
        [JsonPropertyName("dealStatus")] public string? DealStatus { get; init; }
        [JsonPropertyName("epic")] public string? Epic { get; init; }
        [JsonPropertyName("dealReference")] public string? DealReference { get; init; }
        [JsonPropertyName("dealId")] public string? DealId { get; init; }
        [JsonPropertyName("affectedDeals")] public List<AffectedDealRow>? AffectedDeals { get; init; }
        [JsonPropertyName("level")] public decimal? Level { get; init; }
        [JsonPropertyName("size")] public decimal? Size { get; init; }
        [JsonPropertyName("direction")] public string? Direction { get; init; }
        [JsonPropertyName("reason")] public string? Reason { get; init; }
    }

    private class AffectedDealRow
    {
        [JsonPropertyName("dealId")] public string? DealId { get; init; }
        [JsonPropertyName("status")] public string? Status { get; init; }
    }

    private class OpenPositionsResponse
    {
        [JsonPropertyName("positions")] public List<OpenPositionRow> Positions { get; init; } = [];
    }

    private class OpenPositionRow
    {
        [JsonPropertyName("position")] public PositionRow? Position { get; init; }
        [JsonPropertyName("market")] public PositionMarketRow? Market { get; init; }
    }

    private class PositionRow
    {
        [JsonPropertyName("dealId")] public string? DealId { get; init; }
        [JsonPropertyName("epic")] public string? Epic { get; init; }
        [JsonPropertyName("direction")] public string? Direction { get; init; }
        [JsonPropertyName("size")] public decimal? Size { get; init; }
        [JsonPropertyName("level")] public decimal? Level { get; init; }
        [JsonPropertyName("stopLevel")] public decimal? StopLevel { get; init; }
        [JsonPropertyName("profitLevel")] public decimal? ProfitLevel { get; init; }
    }

    private class PositionMarketRow
    {
        [JsonPropertyName("epic")] public string? Epic { get; init; }
        [JsonPropertyName("currency")] public string? Currency { get; init; }
    }
}
