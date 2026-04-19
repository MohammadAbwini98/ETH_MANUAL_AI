using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using Microsoft.Extensions.Configuration;

namespace EthSignal.Infrastructure.Trading;

public sealed class TradeExecutionPolicy : ITradeExecutionPolicy
{
    private readonly IConfiguration _config;
    private readonly ICapitalTradingClient _capitalClient;
    private readonly IExecutedTradeRepository _tradeRepo;
    private readonly IAccountSnapshotService _accountSnapshotService;

    public TradeExecutionPolicy(
        IConfiguration config,
        ICapitalTradingClient capitalClient,
        IExecutedTradeRepository tradeRepo,
        IAccountSnapshotService accountSnapshotService)
    {
        _config = config;
        _capitalClient = capitalClient;
        _tradeRepo = tradeRepo;
        _accountSnapshotService = accountSnapshotService;
    }

    public TradeExecutionPolicySettings GetSettings()
    {
        var allowed = (_config["CapitalTrading:AllowedSourceTypes"] ?? "Recommended")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => Enum.TryParse<SignalExecutionSourceType>(v, true, out var parsed) ? parsed : (SignalExecutionSourceType?)null)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToHashSet();

        if (allowed.Count == 0)
            allowed.Add(SignalExecutionSourceType.Recommended);

        return new TradeExecutionPolicySettings
        {
            Enabled = GetBool("CapitalTrading:Enabled", false),
            AutoExecuteEnabled = GetBool("CapitalTrading:AutoExecuteEnabled", false),
            DemoOnly = GetBool("CapitalTrading:DemoOnly", true),
            StaleWindowMinutes = GetInt("CapitalTrading:StaleWindowMinutes", 30),
            EntryDriftTolerancePct = GetDecimal("CapitalTrading:EntryDriftTolerancePct", 0.005m),
            MaxConcurrentOpenTrades = GetInt("CapitalTrading:MaxConcurrentOpenTrades", 3),
            EntryMode = Enum.TryParse<TradeEntryMode>(_config["CapitalTrading:EntryMode"], true, out var mode)
                ? mode
                : TradeEntryMode.NearRecommendedEntry,
            AllowedSourceTypes = allowed
        };
    }

    public async Task<TradeExecutionPolicyDecision> EvaluateAsync(TradeExecutionRequest request, CancellationToken ct = default)
    {
        var settings = GetSettings();
        var candidate = request.Candidate;

        if (!settings.Enabled)
            return Reject("Trade execution is disabled in configuration.", "ExecutionDisabled");

        if (settings.DemoOnly && !_capitalClient.IsDemoEnvironment)
            return Reject("Capital trading is configured for demo only; non-demo base URL rejected.", "DemoOnlyGuard");

        if (!settings.AllowedSourceTypes.Contains(candidate.SourceType))
            return Reject($"Source type {candidate.SourceType} is not allowed by execution policy.", "SourceTypeNotAllowed");

        if (candidate.Direction is not (SignalDirection.BUY or SignalDirection.SELL))
            return Reject("Only BUY and SELL signals are executable.", "InvalidDirection");

        var age = DateTimeOffset.UtcNow - candidate.SignalTimeUtc;
        if (age > TimeSpan.FromMinutes(settings.StaleWindowMinutes))
            return Reject($"Signal is stale ({age.TotalMinutes:F1}m > {settings.StaleWindowMinutes}m).", "SignalStale");

        if (candidate.RecommendedEntryPrice <= 0 || candidate.TpPrice <= 0 || candidate.SlPrice <= 0)
            return Reject("Entry, TP, and SL must all be positive.", "InvalidLevels");

        var existing = await _tradeRepo.GetBySourceSignalAsync(candidate.SignalId, candidate.SourceType, ct);
        if (existing != null && existing.Status is not (ExecutedTradeStatus.Failed or ExecutedTradeStatus.Rejected or ExecutedTradeStatus.ValidationFailed))
            return Reject($"Signal {candidate.SignalId} already has an execution record with status {existing.Status}.", "DuplicateExecution");

        var openTrades = await _tradeRepo.GetOpenExecutedTradeCountAsync(ct);
        if (openTrades >= settings.MaxConcurrentOpenTrades)
            return Reject($"Max concurrent open trades reached ({openTrades}/{settings.MaxConcurrentOpenTrades}).", "MaxConcurrentOpenTrades");

        await _capitalClient.EnsureDemoReadyAsync(ct);
        var epic = ResolveEpic(candidate.Symbol);
        var market = await _capitalClient.GetMarketInfoAsync(epic, ct);
        if (!market.Tradeable)
            return Reject($"Instrument {epic} is not tradeable.", "InstrumentNotTradeable");

        var account = await _accountSnapshotService.GetLatestAsync(ct);
        if (!account.IsDemo && settings.DemoOnly)
            return Reject("Active Capital account is not a demo account.", "AccountNotDemo");

        var requestedSize = request.RequestedSize.GetValueOrDefault(market.MinDealSize);
        if (requestedSize <= 0)
            requestedSize = market.MinDealSize;
        var finalSize = RoundSize(requestedSize, market.MinDealSize, market.MinSizeIncrement);
        if (finalSize < market.MinDealSize)
            finalSize = market.MinDealSize;

        var marketEntry = candidate.Direction == SignalDirection.BUY ? market.Offer : market.Bid;
        if (marketEntry <= 0)
            return Reject($"Instrument {epic} does not have a valid market price.", "InvalidMarketPrice");

        var driftPct = candidate.RecommendedEntryPrice > 0
            ? Math.Abs(marketEntry - candidate.RecommendedEntryPrice) / candidate.RecommendedEntryPrice
            : decimal.MaxValue;

        var entryMode = request.ForceMarketExecution ? TradeEntryMode.MarketNow : settings.EntryMode;
        if (entryMode != TradeEntryMode.MarketNow && driftPct > settings.EntryDriftTolerancePct)
            return Reject(
                $"Price drift {driftPct:P2} exceeds tolerance {settings.EntryDriftTolerancePct:P2}.",
                "EntryDriftExceeded");

        var stopLevel = candidate.SlPrice;
        var profitLevel = candidate.TpPrice;
        var note = "Using persisted signal TP/SL as source of truth.";

        if (!LevelsAreDirectional(candidate.Direction, marketEntry, profitLevel, stopLevel))
        {
            var delta = marketEntry - candidate.RecommendedEntryPrice;
            profitLevel += delta;
            stopLevel += delta;
            note = "Re-anchored TP/SL to the current market entry because the persisted levels no longer matched the executable entry.";
        }

        if (!LevelsAreDirectional(candidate.Direction, marketEntry, profitLevel, stopLevel))
            return Reject("TP/SL are not directionally valid for the executable entry price.", "InvalidTpSl");

        var minDistance = ResolveDistance(marketEntry, market.MinStopOrProfitDistance, market.MinStopOrProfitDistanceUnit);
        if (Math.Abs(profitLevel - marketEntry) < minDistance || Math.Abs(stopLevel - marketEntry) < minDistance)
            return Reject($"TP/SL are too close to market price; minimum distance is {minDistance:F4}.", "TpSlTooClose");

        var estimatedMargin = market.MarginFactor > 0
            ? marketEntry * finalSize * (market.MarginFactorUnit.Equals("PERCENTAGE", StringComparison.OrdinalIgnoreCase)
                ? market.MarginFactor / 100m
                : market.MarginFactor)
            : 0m;
        if (estimatedMargin > 0 && account.Available > 0 && estimatedMargin > account.Available)
            return Reject($"Estimated margin {estimatedMargin:F2} exceeds available funds {account.Available:F2}.", "InsufficientFunds");

        return new TradeExecutionPolicyDecision
        {
            Allowed = true,
            Message = "Execution candidate passed validation.",
            Plan = new TradeExecutionPlan
            {
                Candidate = candidate,
                Epic = epic,
                InstrumentName = market.InstrumentName,
                RequestedSize = requestedSize,
                FinalSize = finalSize,
                RequestedEntryPrice = candidate.RecommendedEntryPrice,
                MarketEntryPrice = RoundPrice(marketEntry, market.DecimalPlaces),
                ProfitLevel = RoundPrice(profitLevel, market.DecimalPlaces),
                StopLevel = RoundPrice(stopLevel, market.DecimalPlaces),
                Currency = market.Currency,
                AccountSnapshot = account,
                ValidationNote = note
            }
        };
    }

    private string ResolveEpic(string symbol)
        => _config[$"CapitalTrading:InstrumentEpicMap:{symbol}"]
            ?? _config["CapitalApi:Epic"]
            ?? symbol;

    private bool GetBool(string key, bool fallback)
        => bool.TryParse(_config[key], out var value) ? value : fallback;

    private int GetInt(string key, int fallback)
        => int.TryParse(_config[key], out var value) ? value : fallback;

    private decimal GetDecimal(string key, decimal fallback)
        => decimal.TryParse(_config[key], out var value) ? value : fallback;

    private static TradeExecutionPolicyDecision Reject(string message, string failureReason) => new()
    {
        Allowed = false,
        Message = message,
        FailureReason = failureReason
    };

    private static bool LevelsAreDirectional(SignalDirection direction, decimal entry, decimal tp, decimal sl)
        => direction switch
        {
            SignalDirection.BUY => tp > entry && sl < entry,
            SignalDirection.SELL => tp < entry && sl > entry,
            _ => false
        };

    private static decimal RoundSize(decimal size, decimal minDealSize, decimal increment)
    {
        var step = increment > 0 ? increment : minDealSize > 0 ? minDealSize : 1m;
        var steps = Math.Ceiling(size / step);
        return steps * step;
    }

    private static decimal ResolveDistance(decimal entry, decimal rawDistance, string unit)
    {
        if (rawDistance <= 0 || entry <= 0)
            return 0;

        return unit.Equals("PERCENTAGE", StringComparison.OrdinalIgnoreCase)
            ? entry * rawDistance / 100m
            : rawDistance;
    }

    private static decimal RoundPrice(decimal price, int decimals)
    {
        if (decimals < 0)
            return price;
        return Math.Round(price, decimals, MidpointRounding.AwayFromZero);
    }
}
