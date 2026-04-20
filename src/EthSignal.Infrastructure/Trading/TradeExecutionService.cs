using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using Microsoft.Extensions.Logging;

namespace EthSignal.Infrastructure.Trading;

public sealed class TradeExecutionService : ITradeExecutionService
{
    private readonly ITradeExecutionPolicy _policy;
    private readonly ICapitalTradingClient _capitalClient;
    private readonly IExecutedTradeRepository _repository;
    private readonly TradeExecutionRuntimeState _runtimeState;
    private readonly ILogger<TradeExecutionService> _logger;

    public TradeExecutionService(
        ITradeExecutionPolicy policy,
        ICapitalTradingClient capitalClient,
        IExecutedTradeRepository repository,
        TradeExecutionRuntimeState runtimeState,
        ILogger<TradeExecutionService> logger)
    {
        _policy = policy;
        _capitalClient = capitalClient;
        _repository = repository;
        _runtimeState = runtimeState;
        _logger = logger;
    }

    public async Task<TradeExecutionResult> ExecuteAsync(TradeExecutionRequest request, CancellationToken ct = default)
    {
        var candidate = request.Candidate;
        TradeExecutionPolicyDecision policyDecision;
        try
        {
            policyDecision = await _policy.EvaluateAsync(request, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TradeExecution] Policy evaluation failed for signal {SignalId} ({SourceType})", candidate.SignalId, candidate.SourceType);
            _runtimeState.RecordBrokerError(ex.Message);

            var failedTrade = new ExecutedTrade
            {
                SignalId = candidate.SignalId,
                EvaluationId = candidate.EvaluationId,
                SourceType = candidate.SourceType,
                Symbol = candidate.Symbol,
                Instrument = candidate.Symbol,
                Timeframe = candidate.Timeframe,
                Direction = candidate.Direction,
                RecommendedEntryPrice = candidate.RecommendedEntryPrice,
                ActualEntryPrice = 0m,
                TpPrice = candidate.TpPrice,
                SlPrice = candidate.SlPrice,
                RequestedSize = request.RequestedSize ?? 0m,
                ExecutedSize = 0m,
                Status = ExecutedTradeStatus.Failed,
                AccountCurrency = "",
                FailureReason = "PolicyEvaluationFailed",
                ErrorDetails = ex.Message,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            var tradeId = await _repository.InsertExecutedTradeAsync(failedTrade, ct);
            await _repository.InsertExecutionAttemptAsync(
                tradeId,
                candidate.SignalId,
                candidate.SourceType,
                "policy_evaluation",
                success: false,
                summary: "Execution policy evaluation failed.",
                errorDetails: ex.Message,
                brokerPayload: null,
                ct);
            await _repository.InsertExecutionEventAsync(
                tradeId,
                candidate.SignalId,
                candidate.SourceType,
                "execution_failed",
                ex.Message,
                null,
                ct);

            return new TradeExecutionResult
            {
                Success = false,
                ExecutedTradeId = tradeId,
                Status = ExecutedTradeStatus.Failed,
                FailureReason = "PolicyEvaluationFailed",
                ErrorDetails = ex.Message,
                Message = ex.Message
            };
        }

        if (!policyDecision.Allowed || policyDecision.Plan == null)
        {
            var failedTrade = new ExecutedTrade
            {
                SignalId = candidate.SignalId,
                EvaluationId = candidate.EvaluationId,
                SourceType = candidate.SourceType,
                Symbol = candidate.Symbol,
                Instrument = candidate.Symbol,
                Timeframe = candidate.Timeframe,
                Direction = candidate.Direction,
                RecommendedEntryPrice = candidate.RecommendedEntryPrice,
                ActualEntryPrice = 0m,
                TpPrice = candidate.TpPrice,
                SlPrice = candidate.SlPrice,
                RequestedSize = request.RequestedSize ?? 0m,
                ExecutedSize = 0m,
                Status = ExecutedTradeStatus.ValidationFailed,
                AccountCurrency = "",
                FailureReason = policyDecision.FailureReason,
                ErrorDetails = policyDecision.Message,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            var tradeId = await _repository.InsertExecutedTradeAsync(failedTrade, ct);
            await _repository.InsertExecutionAttemptAsync(
                tradeId,
                candidate.SignalId,
                candidate.SourceType,
                "validation",
                success: false,
                summary: policyDecision.FailureReason,
                errorDetails: policyDecision.Message,
                brokerPayload: null,
                ct);
            await _repository.InsertExecutionEventAsync(
                tradeId,
                candidate.SignalId,
                candidate.SourceType,
                "validation_failed",
                policyDecision.Message,
                null,
                ct);
            _runtimeState.RecordBrokerError(policyDecision.Message);
            return new TradeExecutionResult
            {
                Success = false,
                ExecutedTradeId = tradeId,
                Status = ExecutedTradeStatus.ValidationFailed,
                FailureReason = policyDecision.FailureReason,
                ErrorDetails = policyDecision.Message,
                Message = policyDecision.Message
            };
        }

        var plan = policyDecision.Plan;
        var executionAccount = plan.AccountSnapshot;
        _runtimeState.RecordExecutionAccount(executionAccount);

        var pendingTrade = new ExecutedTrade
        {
            SignalId = candidate.SignalId,
            EvaluationId = candidate.EvaluationId,
            SourceType = candidate.SourceType,
            Symbol = candidate.Symbol,
            Instrument = plan.Epic,
            Timeframe = candidate.Timeframe,
            Direction = candidate.Direction,
            RecommendedEntryPrice = candidate.RecommendedEntryPrice,
            ActualEntryPrice = 0m,
            TpPrice = plan.ProfitLevel,
            SlPrice = plan.StopLevel,
            RequestedSize = plan.RequestedSize,
            ExecutedSize = 0m,
            Status = ExecutedTradeStatus.Pending,
            AccountId = executionAccount.AccountId,
            AccountName = executionAccount.AccountName,
            IsDemo = executionAccount.IsDemo,
            AccountCurrency = plan.Currency,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        var executedTradeId = await _repository.InsertExecutedTradeAsync(pendingTrade, ct);
        await _repository.InsertExecutionEventAsync(
            executedTradeId,
            candidate.SignalId,
            candidate.SourceType,
            "execution_requested",
            $"Execution requested by {request.RequestedBy}.",
            null,
            ct);

        try
        {
            var openResult = await _capitalClient.PlacePositionAsync(new CapitalPlacePositionRequest
            {
                Epic = plan.Epic,
                Direction = candidate.Direction,
                Size = plan.FinalSize,
                ProfitLevel = plan.ProfitLevel,
                StopLevel = plan.StopLevel
            }, ct);
            await _repository.InsertExecutionAttemptAsync(
                executedTradeId,
                candidate.SignalId,
                candidate.SourceType,
                "place_position",
                success: true,
                summary: "Order submitted to broker.",
                errorDetails: null,
                brokerPayload: openResult.Note,
                ct);
            _runtimeState.RecordOrderNote(openResult.Note ?? $"Deal reference {openResult.DealReference}");

            var submittedTrade = pendingTrade with
            {
                ExecutedTradeId = executedTradeId,
                DealReference = openResult.DealReference,
                Status = ExecutedTradeStatus.Submitted,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await _repository.UpdateExecutedTradeAsync(submittedTrade, ct);

            var confirmation = await _capitalClient.ConfirmDealAsync(openResult.DealReference, ct);
            await _repository.InsertExecutionAttemptAsync(
                executedTradeId,
                candidate.SignalId,
                candidate.SourceType,
                "confirm_position",
                success: confirmation.Accepted,
                summary: confirmation.Accepted ? "Broker deal confirmed." : confirmation.RejectionReason,
                errorDetails: confirmation.Note,
                brokerPayload: confirmation.DealId,
                ct);

            if (!confirmation.Accepted || string.IsNullOrWhiteSpace(confirmation.DealId))
            {
                var failedTrade = submittedTrade with
                {
                    Status = ExecutedTradeStatus.Rejected,
                    FailureReason = confirmation.RejectionReason ?? "Deal confirmation rejected.",
                    ErrorDetails = confirmation.Note,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
                await _repository.UpdateExecutedTradeAsync(failedTrade, ct);
                await _repository.InsertExecutionEventAsync(
                    executedTradeId,
                    candidate.SignalId,
                    candidate.SourceType,
                    "execution_rejected",
                    failedTrade.FailureReason ?? "Deal rejected.",
                    null,
                    ct);
                _runtimeState.RecordBrokerError(failedTrade.FailureReason ?? "Deal confirmation rejected.");

                return new TradeExecutionResult
                {
                    Success = false,
                    ExecutedTradeId = executedTradeId,
                    Status = ExecutedTradeStatus.Rejected,
                    DealReference = openResult.DealReference,
                    FailureReason = failedTrade.FailureReason,
                    ErrorDetails = failedTrade.ErrorDetails,
                    Message = failedTrade.FailureReason ?? "Deal confirmation rejected."
                };
            }

            var actualEntry = confirmation.Level ?? plan.MarketEntryPrice;
            var openTrade = submittedTrade with
            {
                DealId = confirmation.DealId,
                ActualEntryPrice = actualEntry,
                ExecutedSize = confirmation.Size ?? plan.FinalSize,
                Status = ExecutedTradeStatus.Open,
                OpenedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await _repository.UpdateExecutedTradeAsync(openTrade, ct);
            await _repository.InsertExecutionEventAsync(
                executedTradeId,
                candidate.SignalId,
                candidate.SourceType,
                "execution_opened",
                $"Deal {confirmation.DealId} confirmed open.",
                null,
                ct);

            return new TradeExecutionResult
            {
                Success = true,
                ExecutedTradeId = executedTradeId,
                Status = ExecutedTradeStatus.Open,
                DealReference = openResult.DealReference,
                DealId = confirmation.DealId,
                ActualEntryPrice = actualEntry,
                ExecutedSize = confirmation.Size ?? plan.FinalSize,
                Message = $"Trade opened on Capital.com demo account '{executionAccount.AccountName}'."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TradeExecution] Failed to execute signal {SignalId} ({SourceType})", candidate.SignalId, candidate.SourceType);
            _runtimeState.RecordBrokerError(ex.Message);

            var failedTrade = pendingTrade with
            {
                ExecutedTradeId = executedTradeId,
                Status = ExecutedTradeStatus.Failed,
                FailureReason = "BrokerExecutionFailed",
                ErrorDetails = ex.Message,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await _repository.UpdateExecutedTradeAsync(failedTrade, ct);
            await _repository.InsertExecutionAttemptAsync(
                executedTradeId,
                candidate.SignalId,
                candidate.SourceType,
                "execute",
                success: false,
                summary: "Broker execution failed.",
                errorDetails: ex.Message,
                brokerPayload: null,
                ct);
            await _repository.InsertExecutionEventAsync(
                executedTradeId,
                candidate.SignalId,
                candidate.SourceType,
                "execution_failed",
                ex.Message,
                null,
                ct);

            return new TradeExecutionResult
            {
                Success = false,
                ExecutedTradeId = executedTradeId,
                Status = ExecutedTradeStatus.Failed,
                FailureReason = "BrokerExecutionFailed",
                ErrorDetails = ex.Message,
                Message = ex.Message
            };
        }
    }

    public async Task<ForceCloseResult> ForceCloseAsync(long executedTradeId, ForceCloseRequest request, CancellationToken ct = default)
    {
        var trade = await _repository.GetExecutedTradeAsync(executedTradeId, ct);
        if (trade == null)
            return new ForceCloseResult { Success = false, Message = "Executed trade not found." };
        if (string.IsNullOrWhiteSpace(trade.DealId))
            return new ForceCloseResult { Success = false, Message = "Executed trade does not have a broker deal id." };
        if (trade.Status != ExecutedTradeStatus.Open)
            return new ForceCloseResult { Success = false, Message = $"Trade status {trade.Status} is not force-closeable." };

        var pendingClose = trade with
        {
            Status = ExecutedTradeStatus.CloseRequested,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        await _repository.UpdateExecutedTradeAsync(pendingClose, ct);

        try
        {
            var closeResult = await _capitalClient.ClosePositionAsync(new CapitalClosePositionRequest
            {
                DealId = trade.DealId
            }, ct);
            await _repository.InsertExecutionAttemptAsync(
                executedTradeId,
                trade.SignalId,
                trade.SourceType,
                "close_position",
                success: true,
                summary: "Close request submitted to broker.",
                errorDetails: null,
                brokerPayload: closeResult.Note,
                ct);

            var confirmation = await _capitalClient.ConfirmDealAsync(closeResult.DealReference, ct);
            await _repository.InsertExecutionAttemptAsync(
                executedTradeId,
                trade.SignalId,
                trade.SourceType,
                "confirm_close",
                success: confirmation.Accepted,
                summary: confirmation.Accepted ? "Close confirmed." : confirmation.RejectionReason,
                errorDetails: confirmation.Note,
                brokerPayload: confirmation.DealId,
                ct);

            if (!confirmation.Accepted)
            {
                var closeFailed = pendingClose with
                {
                    Status = ExecutedTradeStatus.CloseFailed,
                    FailureReason = confirmation.RejectionReason ?? "Close confirmation rejected.",
                    ErrorDetails = confirmation.Note,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
                await _repository.UpdateExecutedTradeAsync(closeFailed, ct);
                var failedResult = new ForceCloseResult
                {
                    Success = false,
                    Message = closeFailed.FailureReason ?? "Close confirmation rejected.",
                    DealReference = closeResult.DealReference,
                    DealId = confirmation.DealId
                };
                await _repository.InsertCloseTradeActionAsync(executedTradeId, request, failedResult, ct);
                return failedResult;
            }

            var closeLevel = confirmation.Level;
            var pnl = closeLevel.HasValue
                ? ComputePnl(trade.Direction, trade.ActualEntryPrice, closeLevel.Value, trade.ExecutedSize)
                : trade.Pnl;

            var closedTrade = pendingClose with
            {
                Status = ExecutedTradeStatus.Closed,
                ForceClosed = true,
                CloseSource = TradeCloseSource.User,
                ClosedAtUtc = DateTimeOffset.UtcNow,
                Pnl = pnl,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await _repository.UpdateExecutedTradeAsync(closedTrade, ct);
            var successResult = new ForceCloseResult
            {
                Success = true,
                Message = "Trade force-closed successfully.",
                DealReference = closeResult.DealReference,
                DealId = confirmation.DealId,
                CloseLevel = closeLevel,
                Pnl = pnl
            };
            await _repository.InsertCloseTradeActionAsync(executedTradeId, request, successResult, ct);
            await _repository.InsertExecutionEventAsync(
                executedTradeId,
                trade.SignalId,
                trade.SourceType,
                "force_closed",
                successResult.Message,
                null,
                ct);
            return successResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TradeExecution] Force close failed for executed trade {TradeId}", executedTradeId);
            _runtimeState.RecordBrokerError(ex.Message);
            var closeFailed = pendingClose with
            {
                Status = ExecutedTradeStatus.CloseFailed,
                FailureReason = "CloseFailed",
                ErrorDetails = ex.Message,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await _repository.UpdateExecutedTradeAsync(closeFailed, ct);
            var failed = new ForceCloseResult { Success = false, Message = ex.Message };
            await _repository.InsertCloseTradeActionAsync(executedTradeId, request, failed, ct);
            return failed;
        }
    }

    private static decimal ComputePnl(SignalDirection direction, decimal entry, decimal exit, decimal size)
        => direction == SignalDirection.BUY
            ? (exit - entry) * size
            : (entry - exit) * size;
}
