using System.Text.Json;
using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using Microsoft.Extensions.Logging;

namespace EthSignal.Infrastructure.Trading;

public sealed class TradeLifecycleReconciliationService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly ICapitalTradingClient _capitalClient;
    private readonly IExecutedTradeRepository _repository;
    private readonly ILogger<TradeLifecycleReconciliationService> _logger;

    public TradeLifecycleReconciliationService(
        ICapitalTradingClient capitalClient,
        IExecutedTradeRepository repository,
        ILogger<TradeLifecycleReconciliationService> logger)
    {
        _capitalClient = capitalClient;
        _repository = repository;
        _logger = logger;
    }

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        var trades = await _repository.GetTradesForLifecycleReconciliationAsync(250, ct);
        if (trades.Count == 0)
            return;

        var openPositions = await _capitalClient.GetOpenPositionsAsync(ct);
        var positionsByDealId = openPositions
            .Where(position => !string.IsNullOrWhiteSpace(position.DealId))
            .GroupBy(position => position.DealId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var positionsByReference = openPositions
            .Where(position => !string.IsNullOrWhiteSpace(position.DealReference))
            .GroupBy(position => position.DealReference!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var trade in trades.OrderBy(trade => trade.UpdatedAtUtc))
        {
            switch (trade.Status)
            {
                case ExecutedTradeStatus.Pending:
                case ExecutedTradeStatus.Submitted:
                    await ReconcilePendingOrSubmittedAsync(trade, positionsByDealId, positionsByReference, ct);
                    break;

                case ExecutedTradeStatus.Open:
                case ExecutedTradeStatus.CloseRequested:
                    await ReconcileActiveOpenTradeAsync(trade, positionsByDealId, positionsByReference, ct);
                    break;
            }
        }
    }

    private async Task ReconcilePendingOrSubmittedAsync(
        ExecutedTrade trade,
        IReadOnlyDictionary<string, CapitalPositionSnapshot> positionsByDealId,
        IReadOnlyDictionary<string, CapitalPositionSnapshot> positionsByReference,
        CancellationToken ct)
    {
        if (TryGetMatchingPosition(trade, positionsByDealId, positionsByReference, out var matchedPosition))
        {
            await MarkTradeOpenFromPositionAsync(trade, matchedPosition!, "pending_open_reconciled", ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(trade.DealReference))
            return;

        var confirmation = await TryConfirmDealAsync(trade.DealReference, ct);
        if (confirmation == null)
            return;

        if (confirmation.Accepted && !string.IsNullOrWhiteSpace(confirmation.DealId))
        {
            await MarkTradeOpenFromConfirmationAsync(trade, confirmation, "pending_open_confirmed", ct);
            return;
        }

        if (IsRejectedConfirmation(confirmation))
        {
            var rejectedTrade = trade with
            {
                Status = ExecutedTradeStatus.Rejected,
                FailureReason = confirmation.RejectionReason ?? "Deal confirmation rejected.",
                ErrorDetails = confirmation.Note,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await _repository.UpdateExecutedTradeAsync(rejectedTrade, ct);
            await _repository.InsertExecutionEventAsync(
                trade.ExecutedTradeId,
                trade.SignalId,
                trade.SourceType,
                "pending_rejected",
                rejectedTrade.FailureReason ?? "Pending trade rejected by broker.",
                Serialize(new
                {
                    confirmation.Status,
                    confirmation.DealStatus,
                    confirmation.RejectionReason
                }),
                ct);

            _logger.LogWarning(
                "[TradeLifecycle] {SourceType} trade {TradeId} signal {SignalId} was rejected while pending: {Reason}",
                trade.SourceType,
                trade.ExecutedTradeId,
                trade.SignalId,
                rejectedTrade.FailureReason);
            return;
        }

        _logger.LogInformation(
            "[TradeLifecycle] {SourceType} trade {TradeId} signal {SignalId} still pending broker confirmation (DealReference={DealReference}, Status={Status}, DealStatus={DealStatus})",
            trade.SourceType,
            trade.ExecutedTradeId,
            trade.SignalId,
            trade.DealReference,
            confirmation.Status,
            confirmation.DealStatus);
    }

    private async Task ReconcileActiveOpenTradeAsync(
        ExecutedTrade trade,
        IReadOnlyDictionary<string, CapitalPositionSnapshot> positionsByDealId,
        IReadOnlyDictionary<string, CapitalPositionSnapshot> positionsByReference,
        CancellationToken ct)
    {
        if (TryGetMatchingPosition(trade, positionsByDealId, positionsByReference, out var matchedPosition))
        {
            if (trade.Status != ExecutedTradeStatus.Open)
            {
                _logger.LogWarning(
                    "[TradeLifecycle] Broker/local mismatch for trade {TradeId}: local status {Status}, broker still shows position open",
                    trade.ExecutedTradeId,
                    trade.Status);
                await _repository.InsertExecutionEventAsync(
                    trade.ExecutedTradeId,
                    trade.SignalId,
                    trade.SourceType,
                    "broker_local_mismatch",
                    $"Local status {trade.Status} disagreed with broker open position state.",
                    Serialize(new
                    {
                        brokerDealId = matchedPosition!.DealId,
                        brokerDealReference = matchedPosition.DealReference
                    }),
                    ct);
            }

            return;
        }

        CapitalPositionSnapshot? brokerPosition = null;
        if (!string.IsNullOrWhiteSpace(trade.DealId))
            brokerPosition = await _capitalClient.GetPositionAsync(trade.DealId, ct);

        if (brokerPosition != null)
        {
            await MarkTradeOpenFromPositionAsync(trade, brokerPosition, "broker_position_sync", ct);
            return;
        }

        var closeActivity = await TryGetLatestCloseActivityAsync(trade, ct);
        if (closeActivity != null)
        {
            await MarkTradeClosedAsync(trade, closeActivity, ct);
            return;
        }
        await _repository.InsertExecutionEventAsync(
            trade.ExecutedTradeId,
            trade.SignalId,
            trade.SourceType,
            "close_unconfirmed",
            "Trade is absent from broker open positions, but no Capital close activity has confirmed the final outcome yet.",
            Serialize(new
            {
                trade.DealId,
                trade.DealReference
            }),
            ct);

        _logger.LogInformation(
            "[TradeLifecycle] {SourceType} trade {TradeId} signal {SignalId} is absent from broker open positions but remains {Status} until Capital close activity confirms the final state",
            trade.SourceType,
            trade.ExecutedTradeId,
            trade.SignalId,
            trade.Status);
    }

    private async Task MarkTradeOpenFromPositionAsync(
        ExecutedTrade trade,
        CapitalPositionSnapshot position,
        string eventType,
        CancellationToken ct)
    {
        var updatedTrade = trade with
        {
            DealId = string.IsNullOrWhiteSpace(trade.DealId) ? position.DealId : trade.DealId,
            DealReference = string.IsNullOrWhiteSpace(trade.DealReference) ? position.DealReference : trade.DealReference,
            ActualEntryPrice = position.Level > 0 ? position.Level : trade.ActualEntryPrice,
            ExecutedSize = position.Size > 0 ? position.Size : trade.ExecutedSize,
            Status = ExecutedTradeStatus.Open,
            OpenedAtUtc = trade.OpenedAtUtc ?? DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        await _repository.UpdateExecutedTradeAsync(updatedTrade, ct);
        await _repository.InsertExecutionEventAsync(
            trade.ExecutedTradeId,
            trade.SignalId,
            trade.SourceType,
            eventType,
            $"Trade opened on broker deal {updatedTrade.DealId}.",
            Serialize(new
            {
                position.DealId,
                position.DealReference,
                position.Level,
                position.Size
            }),
            ct);

        _logger.LogInformation(
            "[TradeLifecycle] {SourceType} trade {TradeId} signal {SignalId} transitioned {PreviousStatus} -> Open from broker position sync",
            trade.SourceType,
            trade.ExecutedTradeId,
            trade.SignalId,
            trade.Status);
    }

    private async Task MarkTradeOpenFromConfirmationAsync(
        ExecutedTrade trade,
        CapitalDealConfirmation confirmation,
        string eventType,
        CancellationToken ct)
    {
        var actualEntry = confirmation.Level ?? trade.ActualEntryPrice;
        var updatedTrade = trade with
        {
            DealId = confirmation.DealId ?? trade.DealId,
            ActualEntryPrice = actualEntry,
            ExecutedSize = confirmation.Size ?? trade.ExecutedSize,
            Status = ExecutedTradeStatus.Open,
            OpenedAtUtc = trade.OpenedAtUtc ?? DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        await _repository.UpdateExecutedTradeAsync(updatedTrade, ct);
        await _repository.InsertExecutionEventAsync(
            trade.ExecutedTradeId,
            trade.SignalId,
            trade.SourceType,
            eventType,
            $"Trade opened after delayed broker confirmation for deal {updatedTrade.DealId}.",
            Serialize(new
            {
                confirmation.Status,
                confirmation.DealStatus,
                confirmation.DealId,
                confirmation.Level,
                confirmation.Size
            }),
            ct);

        _logger.LogInformation(
            "[TradeLifecycle] {SourceType} trade {TradeId} signal {SignalId} transitioned {PreviousStatus} -> Open after delayed confirmation",
            trade.SourceType,
            trade.ExecutedTradeId,
            trade.SignalId,
            trade.Status);
    }

    private async Task MarkTradeClosedAsync(
        ExecutedTrade trade,
        CapitalActivityRecord activity,
        CancellationToken ct)
    {
        var closeSource = MapCloseSource(activity.Source);
        var closeLevel = ResolveConfirmedCloseLevel(trade, activity, closeSource);
        if (!closeLevel.HasValue)
        {
            _logger.LogWarning(
                "[TradeLifecycle] Ignoring non-terminal close activity for trade {TradeId} signal {SignalId}: Source={Source} Status={Status} Level={Level}",
                trade.ExecutedTradeId,
                trade.SignalId,
                activity.Source,
                activity.Status,
                activity.Level);
            return;
        }

        var pnl = closeLevel.HasValue
            ? ComputePnl(trade.Direction, trade.ActualEntryPrice, closeLevel.Value, ResolveTradeSize(trade))
            : trade.Pnl;
        var terminalStatus = DeriveTerminalStatus(closeSource, pnl);

        var updatedTrade = trade with
        {
            Status = terminalStatus,
            CloseSource = closeSource,
            ClosedAtUtc = activity.DateUtc,
            Pnl = pnl,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        await _repository.UpdateExecutedTradeAsync(updatedTrade, ct);
        await _repository.InsertExecutionEventAsync(
            trade.ExecutedTradeId,
            trade.SignalId,
            trade.SourceType,
            "trade_closed_reconciled",
            BuildCloseMessage(updatedTrade, activity),
            Serialize(new
            {
                activity.Source,
                activity.Status,
                activity.Level,
                activity.Size,
                activity.DateUtc
            }),
            ct);

        _logger.LogInformation(
            "[TradeLifecycle] {SourceType} trade {TradeId} signal {SignalId} closed as {Status} via {CloseSource}",
            trade.SourceType,
            trade.ExecutedTradeId,
            trade.SignalId,
            updatedTrade.Status,
            updatedTrade.CloseSource);
    }

    private async Task<CapitalDealConfirmation?> TryConfirmDealAsync(string dealReference, CancellationToken ct)
    {
        try
        {
            return await _capitalClient.ConfirmDealAsync(dealReference, ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("(404)", StringComparison.Ordinal))
        {
            return null;
        }
    }

    private async Task<CapitalActivityRecord?> TryGetLatestCloseActivityAsync(ExecutedTrade trade, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(trade.DealId))
            return null;

        var age = DateTimeOffset.UtcNow - (trade.OpenedAtUtc ?? trade.CreatedAtUtc);
        var lastPeriodSeconds = (int)Math.Clamp(Math.Ceiling(age.TotalSeconds), 60d, 86400d);
        var activities = await _capitalClient.GetActivityHistoryAsync(new CapitalActivityQuery
        {
            DealId = trade.DealId,
            LastPeriodSeconds = lastPeriodSeconds,
            Detailed = true
        }, ct);

        var matching = activities
            .Where(activity => string.Equals(activity.DealId, trade.DealId, StringComparison.Ordinal))
            .ToList();

        var validCloseActivity = matching.FirstOrDefault(activity => IsConfirmedCloseActivity(trade, activity));
        if (validCloseActivity != null)
            return validCloseActivity;

        var suspicious = matching.FirstOrDefault(activity =>
            IsCloseActivitySource(activity.Source)
            && IsClosedActivityStatus(activity.Status));
        if (suspicious != null)
        {
            _logger.LogWarning(
                "[TradeLifecycle] Ignored suspicious broker close activity for trade {TradeId} signal {SignalId}: Source={Source} Status={Status} Level={Level} DateUtc={DateUtc}",
                trade.ExecutedTradeId,
                trade.SignalId,
                suspicious.Source,
                suspicious.Status,
                suspicious.Level,
                suspicious.DateUtc);
        }

        return null;
    }

    private static bool TryGetMatchingPosition(
        ExecutedTrade trade,
        IReadOnlyDictionary<string, CapitalPositionSnapshot> positionsByDealId,
        IReadOnlyDictionary<string, CapitalPositionSnapshot> positionsByReference,
        out CapitalPositionSnapshot? position)
    {
        if (!string.IsNullOrWhiteSpace(trade.DealId)
            && positionsByDealId.TryGetValue(trade.DealId, out position))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(trade.DealReference)
            && positionsByReference.TryGetValue(trade.DealReference, out position))
        {
            return true;
        }

        position = null;
        return false;
    }

    private static bool IsRejectedConfirmation(CapitalDealConfirmation confirmation)
    {
        if (confirmation.Accepted)
            return false;

        var dealStatus = Normalize(confirmation.DealStatus);
        var status = Normalize(confirmation.Status);
        return dealStatus is "REJECTED" or "FAILED"
            || status is "REJECTED" or "FAILED" or "ERROR";
    }

    private static bool IsCloseActivitySource(string? source)
        => Normalize(source) is "TP" or "SL" or "USER" or "SYSTEM" or "CLOSE_OUT";

    private static bool IsClosedActivityStatus(string? status)
        => Normalize(status) is "EXECUTED" or "ACCEPTED" or "PROCESSED";

    private static TradeCloseSource MapCloseSource(string? source)
        => Normalize(source) switch
        {
            "TP" => TradeCloseSource.TakeProfit,
            "SL" => TradeCloseSource.StopLoss,
            "USER" => TradeCloseSource.User,
            "SYSTEM" => TradeCloseSource.Platform,
            "CLOSE_OUT" => TradeCloseSource.Platform,
            _ => TradeCloseSource.Unknown
        };

    private static bool IsConfirmedCloseActivity(ExecutedTrade trade, CapitalActivityRecord activity)
    {
        if (!IsCloseActivitySource(activity.Source) || !IsClosedActivityStatus(activity.Status))
            return false;

        var referenceOpenTime = trade.OpenedAtUtc ?? trade.CreatedAtUtc;
        if (activity.DateUtc < referenceOpenTime)
            return false;

        var closeSource = MapCloseSource(activity.Source);
        var closeLevel = ResolveConfirmedCloseLevel(trade, activity, closeSource);
        return closeLevel.HasValue;
    }

    private static ExecutedTradeStatus DeriveTerminalStatus(TradeCloseSource? closeSource, decimal? pnl)
    {
        if (closeSource == TradeCloseSource.TakeProfit)
            return ExecutedTradeStatus.Win;
        if (closeSource == TradeCloseSource.StopLoss)
            return ExecutedTradeStatus.Loss;
        if (pnl.HasValue)
        {
            if (pnl.Value > 0)
                return ExecutedTradeStatus.Win;
            if (pnl.Value < 0)
                return ExecutedTradeStatus.Loss;
        }

        return ExecutedTradeStatus.Closed;
    }

    private static decimal ComputePnl(SignalDirection direction, decimal entry, decimal exit, decimal size)
        => direction == SignalDirection.BUY
            ? (exit - entry) * size
            : (entry - exit) * size;

    private static decimal ResolveTradeSize(ExecutedTrade trade)
        => trade.ExecutedSize > 0 ? trade.ExecutedSize : trade.RequestedSize;

    private static decimal? ResolveConfirmedCloseLevel(ExecutedTrade trade, CapitalActivityRecord activity, TradeCloseSource closeSource)
    {
        if (IsConfirmedPriceLevel(activity.Level))
            return activity.Level;

        var fallbackLevel = GetFallbackCloseLevel(trade, closeSource);
        return IsConfirmedPriceLevel(fallbackLevel) ? fallbackLevel : null;
    }

    private static decimal? GetFallbackCloseLevel(ExecutedTrade trade, TradeCloseSource closeSource)
        => closeSource switch
        {
            TradeCloseSource.TakeProfit when trade.TpPrice > 0 => trade.TpPrice,
            TradeCloseSource.StopLoss when trade.SlPrice > 0 => trade.SlPrice,
            _ => null
        };

    private static bool IsConfirmedPriceLevel(decimal? level)
        => level.HasValue && level.Value > 0;

    private static string BuildCloseMessage(ExecutedTrade trade, CapitalActivityRecord activity)
        => trade.CloseSource switch
        {
            TradeCloseSource.TakeProfit => $"Trade closed via TP at {FormatLevel(activity.Level ?? trade.TpPrice)}.",
            TradeCloseSource.StopLoss => $"Trade closed via SL at {FormatLevel(activity.Level ?? trade.SlPrice)}.",
            TradeCloseSource.User => "Trade closed manually at the broker.",
            TradeCloseSource.Platform => "Trade closed by the broker/platform.",
            _ => "Trade closed at the broker with an unknown close reason."
        };

    private static string FormatLevel(decimal? level)
        => level.HasValue ? level.Value.ToString("0.####") : "broker-reported level";

    private static string Normalize(string? value)
        => value?.Trim().Replace("-", "_", StringComparison.Ordinal).ToUpperInvariant() ?? "";

    private static string? Serialize(object value)
        => JsonSerializer.Serialize(value, JsonOpts);
}
