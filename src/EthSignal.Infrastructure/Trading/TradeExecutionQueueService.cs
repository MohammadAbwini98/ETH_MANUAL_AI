using System.Text.Json;
using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using Microsoft.Extensions.Logging;

namespace EthSignal.Infrastructure.Trading;

public sealed class TradeExecutionQueueService : ITradeExecutionQueueService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ProcessingRecoveryWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ForceMarketRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan CapacityRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(2);

    private readonly ITradeExecutionQueueRepository _queueRepository;
    private readonly IExecutedTradeRepository _executedTradeRepository;
    private readonly ITradeExecutionPolicy _policy;
    private readonly ITradeExecutionService _executionService;
    private readonly IAccountSnapshotService _accountSnapshotService;
    private readonly ILogger<TradeExecutionQueueService> _logger;
    private readonly SemaphoreSlim _dispatchLock = new(1, 1);
    private readonly SemaphoreSlim _workSignal = new(0, 1);
    private int _workPending;

    public TradeExecutionQueueService(
        ITradeExecutionQueueRepository queueRepository,
        IExecutedTradeRepository executedTradeRepository,
        ITradeExecutionPolicy policy,
        ITradeExecutionService executionService,
        IAccountSnapshotService accountSnapshotService,
        ILogger<TradeExecutionQueueService> logger)
    {
        _queueRepository = queueRepository;
        _executedTradeRepository = executedTradeRepository;
        _policy = policy;
        _executionService = executionService;
        _accountSnapshotService = accountSnapshotService;
        _logger = logger;
    }

    public async Task<TradeExecutionQueueResult> EnqueueAsync(TradeExecutionRequest request, CancellationToken ct = default)
    {
        if (IsExcludedExecutionTimeframe(request.Candidate.Timeframe))
        {
            return new TradeExecutionQueueResult
            {
                Accepted = false,
                Status = "Rejected",
                FailureReason = "TimeframeNotAllowed",
                Message = $"Timeframe {request.Candidate.Timeframe} is excluded from broker execution."
            };
        }

        var existingTrade = await _executedTradeRepository.GetBySourceSignalAsync(request.Candidate.SignalId, request.Candidate.SourceType, ct);
        if (existingTrade != null && !IsRetryableTerminalStatus(existingTrade.Status))
        {
            return new TradeExecutionQueueResult
            {
                Accepted = false,
                ExecutedTradeId = existingTrade.ExecutedTradeId,
                Status = existingTrade.Status.ToString(),
                FailureReason = "DuplicateExecution",
                Message = $"Signal {request.Candidate.SignalId} already has an execution record with status {existingTrade.Status}."
            };
        }

        var existingQueue = await _queueRepository.GetActiveBySourceSignalAsync(request.Candidate.SignalId, request.Candidate.SourceType, ct);
        if (existingQueue != null)
        {
            NotifyWorkAvailable();
            return new TradeExecutionQueueResult
            {
                Accepted = true,
                QueueEntryId = existingQueue.QueueEntryId,
                ExecutedTradeId = existingQueue.ExecutedTradeId,
                Status = existingQueue.Status.ToString(),
                Message = $"Signal {request.Candidate.SignalId} is already queued for execution."
            };
        }

        var now = DateTimeOffset.UtcNow;
        var entry = new QueuedTradeExecution
        {
            SignalId = request.Candidate.SignalId,
            EvaluationId = request.Candidate.EvaluationId,
            SourceType = request.Candidate.SourceType,
            RequestedBy = request.RequestedBy,
            RequestedSize = request.RequestedSize,
            ForceMarketExecution = request.ForceMarketExecution,
            CandidateJson = JsonSerializer.Serialize(request.Candidate, JsonOpts),
            Status = TradeExecutionQueueStatus.Queued,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        var queueEntryId = await _queueRepository.InsertAsync(entry, ct);
        var counts = await _queueRepository.GetStatusCountsAsync(ct);
        _logger.LogInformation(
            "[TradeExecutionQueue] Enqueued {SourceType} signal {SignalId} as queue entry {QueueEntryId} (Queued={QueuedCount}, Processing={ProcessingCount})",
            request.Candidate.SourceType,
            request.Candidate.SignalId,
            queueEntryId,
            counts.QueuedCount,
            counts.ProcessingCount);

        NotifyWorkAvailable();

        return new TradeExecutionQueueResult
        {
            Accepted = true,
            QueueEntryId = queueEntryId,
            Status = TradeExecutionQueueStatus.Queued.ToString(),
            Message = $"Signal {request.Candidate.SignalId} was queued for execution."
        };
    }

    public async Task<int> DrainAsync(CancellationToken ct = default)
    {
        var settings = _policy.GetSettings();
        if (!settings.Enabled)
            return 0;

        if (!await _dispatchLock.WaitAsync(0, ct))
            return 0;

        try
        {
            var recovered = await _queueRepository.RequeueStaleProcessingAsync(DateTimeOffset.UtcNow - ProcessingRecoveryWindow, ct);
            if (recovered > 0)
            {
                _logger.LogWarning(
                    "[TradeExecutionQueue] Recovered {RecoveredCount} stale processing queue entries back to Queued",
                    recovered);
            }

            var context = await GetDispatchContextAsync(settings, ct);
            if (context.QueuedCount == 0)
                return 0;

            if (context.AvailableDispatchSlots <= 0)
            {
                LogBlockedCapacity(context);
                return 0;
            }

            var dispatched = 0;
            while (!ct.IsCancellationRequested && dispatched < context.AvailableDispatchSlots)
            {
                var claimed = await _queueRepository.TryClaimNextQueuedAsync(DateTimeOffset.UtcNow, ct);
                if (claimed == null)
                    break;

                dispatched++;
                _logger.LogInformation(
                    "[TradeExecutionQueue] Dequeued queue entry {QueueEntryId} for {SourceType} signal {SignalId} (Dispatch {DispatchIndex}/{DispatchCapacity})",
                    claimed.QueueEntryId,
                    claimed.SourceType,
                    claimed.SignalId,
                    dispatched,
                    context.AvailableDispatchSlots);

                _ = ProcessEntryInBackgroundAsync(claimed, ct);
            }

            if (dispatched > 0)
            {
                _logger.LogInformation(
                    "[TradeExecutionQueue] Dispatched {DispatchedCount} queued execution request(s) (BrokerOpen={BrokerOpenTradeCount}, PendingSubmitted={PendingSubmissionCount}, Processing={ProcessingCount}, QueuedRemainingApprox={QueuedRemaining})",
                    dispatched,
                    context.BrokerOpenTradeCount,
                    context.PendingSubmissionCount,
                    context.ProcessingCount + dispatched,
                    Math.Max(0, context.QueuedCount - dispatched));
            }

            return dispatched;
        }
        finally
        {
            _dispatchLock.Release();
        }
    }

    public async Task<TradeExecutionQueueSnapshot> GetSnapshotAsync(int limit = 50, CancellationToken ct = default)
    {
        var settings = _policy.GetSettings();
        var context = await GetDispatchContextAsync(settings, ct);
        var serverTimeUtc = DateTimeOffset.UtcNow;
        var entries = await _queueRepository.GetActiveEntriesAsync(limit, ct);

        return new TradeExecutionQueueSnapshot
        {
            ServerTimeUtc = serverTimeUtc,
            ActiveTradeCount = context.BrokerOpenTradeCount + context.PendingSubmissionCount,
            BrokerOpenTradeCount = context.BrokerOpenTradeCount,
            PendingSubmissionCount = context.PendingSubmissionCount,
            MaxConcurrentOpenTrades = settings.MaxConcurrentOpenTrades,
            QueueConcurrentRequestLimit = Math.Clamp(settings.QueueConcurrentRequestLimit, 1, 3),
            AvailableDispatchSlots = context.AvailableDispatchSlots,
            QueuedCount = context.QueuedCount,
            ProcessingCount = context.ProcessingCount,
            CompletedCount = context.CompletedCount,
            FailedCount = context.FailedCount,
            Entries = entries.Select(entry => new TradeExecutionQueueEntrySnapshot
            {
                QueueEntryId = entry.QueueEntryId,
                SignalId = entry.SignalId,
                EvaluationId = entry.EvaluationId,
                SourceType = entry.SourceType,
                RequestedBy = entry.RequestedBy,
                RequestedSize = entry.RequestedSize,
                ForceMarketExecution = entry.ForceMarketExecution,
                Status = entry.Status,
                ExecutedTradeId = entry.ExecutedTradeId,
                FailureReason = entry.FailureReason,
                ErrorDetails = entry.ErrorDetails,
                CreatedAtUtc = entry.CreatedAtUtc,
                UpdatedAtUtc = entry.UpdatedAtUtc,
                ProcessedAtUtc = entry.ProcessedAtUtc,
                AgeSeconds = Math.Max(0d, (serverTimeUtc - entry.CreatedAtUtc).TotalSeconds),
                WaitSeconds = Math.Max(0d, (serverTimeUtc - entry.UpdatedAtUtc).TotalSeconds)
            }).ToList()
        };
    }

    public async Task WaitForWorkAsync(CancellationToken ct = default)
    {
        await _workSignal.WaitAsync(ct);
        Interlocked.Exchange(ref _workPending, 0);
    }

    public void NotifyWorkAvailable()
    {
        if (Interlocked.Exchange(ref _workPending, 1) != 0)
            return;

        try
        {
            _workSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            Interlocked.Exchange(ref _workPending, 1);
        }
    }

    private async Task ProcessEntryInBackgroundAsync(QueuedTradeExecution entry, CancellationToken ct)
    {
        var signalNextDispatch = true;
        try
        {
            signalNextDispatch = await ProcessEntryAsync(entry, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            signalNextDispatch = false;
        }
        catch (Exception ex)
        {
            signalNextDispatch = true;
            _logger.LogError(ex, "[TradeExecutionQueue] Queue entry {QueueEntryId} crashed during execution", entry.QueueEntryId);

            var failed = entry with
            {
                Status = TradeExecutionQueueStatus.Failed,
                FailureReason = "QueueDispatchFailed",
                ErrorDetails = ex.Message,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                ProcessedAtUtc = DateTimeOffset.UtcNow
            };
            await _queueRepository.UpdateAsync(failed, ct);
        }
        finally
        {
            if (signalNextDispatch && !ct.IsCancellationRequested)
                NotifyWorkAvailable();
        }
    }

    private async Task<bool> ProcessEntryAsync(QueuedTradeExecution entry, CancellationToken ct)
    {
        var existingTrade = await _executedTradeRepository.GetBySourceSignalAsync(entry.SignalId, entry.SourceType, ct);
        if (existingTrade != null && !IsRetryableTerminalStatus(existingTrade.Status))
        {
            var duplicate = entry with
            {
                Status = TradeExecutionQueueStatus.Completed,
                ExecutedTradeId = existingTrade.ExecutedTradeId,
                FailureReason = "DuplicateExecution",
                ErrorDetails = $"Existing execution record {existingTrade.ExecutedTradeId} is already {existingTrade.Status}.",
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                ProcessedAtUtc = DateTimeOffset.UtcNow
            };
            await _queueRepository.UpdateAsync(duplicate, ct);
            return true;
        }

        TradeExecutionCandidate? candidate;
        try
        {
            candidate = JsonSerializer.Deserialize<TradeExecutionCandidate>(entry.CandidateJson, JsonOpts);
        }
        catch (Exception ex)
        {
            var invalid = entry with
            {
                Status = TradeExecutionQueueStatus.Failed,
                FailureReason = "QueuePayloadInvalid",
                ErrorDetails = ex.Message,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                ProcessedAtUtc = DateTimeOffset.UtcNow
            };
            await _queueRepository.UpdateAsync(invalid, ct);
            _logger.LogError(ex, "[TradeExecutionQueue] Failed to deserialize queue entry {QueueEntryId}", entry.QueueEntryId);
            return true;
        }

        if (candidate == null)
        {
            var invalid = entry with
            {
                Status = TradeExecutionQueueStatus.Failed,
                FailureReason = "QueuePayloadInvalid",
                ErrorDetails = "Candidate payload was null.",
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                ProcessedAtUtc = DateTimeOffset.UtcNow
            };
            await _queueRepository.UpdateAsync(invalid, ct);
            return true;
        }

        _logger.LogInformation(
            "[TradeExecutionQueue] Starting execution for queue entry {QueueEntryId} ({SourceType} signal {SignalId})",
            entry.QueueEntryId,
            entry.SourceType,
            entry.SignalId);

        var result = await _executionService.ExecuteAsync(new TradeExecutionRequest
        {
            Candidate = candidate,
            RequestedBy = entry.RequestedBy,
            RequestedSize = entry.RequestedSize,
            ForceMarketExecution = entry.ForceMarketExecution
        }, ct);

        if (result.RetryRequested || (!result.Success && string.Equals(result.FailureReason, "MaxConcurrentOpenTrades", StringComparison.Ordinal)))
        {
            await DeferRetryAsync(entry, result, ct);
            return false;
        }

        var processed = entry with
        {
            Status = TradeExecutionQueueStatus.Completed,
            ExecutedTradeId = result.ExecutedTradeId,
            FailureReason = result.FailureReason,
            ErrorDetails = result.ErrorDetails ?? result.Message,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            ProcessedAtUtc = DateTimeOffset.UtcNow
        };
        await _queueRepository.UpdateAsync(processed, ct);

        _logger.LogInformation(
            "[TradeExecutionQueue] Processed queue entry {QueueEntryId} for {SourceType} signal {SignalId} -> {QueueStatus} / TradeStatus={TradeStatus}",
            entry.QueueEntryId,
            entry.SourceType,
            entry.SignalId,
            processed.Status,
            result.Status);

        return true;
    }

    private async Task<DispatchContext> GetDispatchContextAsync(TradeExecutionPolicySettings settings, CancellationToken ct)
    {
        var counts = await _queueRepository.GetStatusCountsAsync(ct);
        var account = await TryGetQueueAccountSnapshotAsync(settings, ct);

        if (account == null)
        {
            return new DispatchContext(
                BrokerOpenTradeCount: 0,
                PendingSubmissionCount: 0,
                QueuedCount: counts.QueuedCount,
                ProcessingCount: counts.ProcessingCount,
                CompletedCount: counts.CompletedCount,
                FailedCount: counts.FailedCount,
                AvailableDispatchSlots: 0);
        }

        var pendingSubmissionCount = await _executedTradeRepository.GetPendingOrSubmittedTradeCountAsync(new ExecutedTradeQuery
        {
            AccountId = account.AccountId,
            AccountName = account.AccountName,
            IsDemo = settings.DemoOnly ? true : null
        }, ct);

        var requestCapacity = Math.Max(0, Math.Clamp(settings.QueueConcurrentRequestLimit, 1, 3) - counts.ProcessingCount);
        var openTradeCapacity = Math.Max(0, settings.MaxConcurrentOpenTrades - account.OpenPositions - pendingSubmissionCount - counts.ProcessingCount);

        return new DispatchContext(
            BrokerOpenTradeCount: account.OpenPositions,
            PendingSubmissionCount: pendingSubmissionCount,
            QueuedCount: counts.QueuedCount,
            ProcessingCount: counts.ProcessingCount,
            CompletedCount: counts.CompletedCount,
            FailedCount: counts.FailedCount,
            AvailableDispatchSlots: Math.Max(0, Math.Min(requestCapacity, openTradeCapacity)));
    }

    private async Task<AccountSnapshot?> TryGetQueueAccountSnapshotAsync(TradeExecutionPolicySettings settings, CancellationToken ct)
    {
        try
        {
            return await _accountSnapshotService.GetLatestAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[TradeExecutionQueue] Failed to refresh broker account snapshot; attempting to use the latest persisted snapshot");
            return settings.DemoOnly
                ? await _executedTradeRepository.GetLatestAccountSnapshotAsync(accountName: null, isDemo: true, ct)
                : await _executedTradeRepository.GetLatestAccountSnapshotAsync(ct);
        }
    }

    private void LogBlockedCapacity(DispatchContext context)
    {
        if (context.ProcessingCount >= Math.Clamp(_policy.GetSettings().QueueConcurrentRequestLimit, 1, 3))
        {
            _logger.LogInformation(
                "[TradeExecutionQueue] Execution blocked because all request slots are in use ({ProcessingCount}/{MaxConcurrentRequests}); queued items remain waiting in FIFO order",
                context.ProcessingCount,
                Math.Clamp(_policy.GetSettings().QueueConcurrentRequestLimit, 1, 3));
            return;
        }

        _logger.LogInformation(
            "[TradeExecutionQueue] Execution blocked because broker open-trade capacity is full (BrokerOpen={BrokerOpenTradeCount}, PendingSubmitted={PendingSubmissionCount}, Processing={ProcessingCount}, MaxOpenTrades={MaxConcurrentOpenTrades}, Queued={QueuedCount})",
            context.BrokerOpenTradeCount,
            context.PendingSubmissionCount,
            context.ProcessingCount,
            _policy.GetSettings().MaxConcurrentOpenTrades,
            context.QueuedCount);
    }

    private static bool IsRetryableTerminalStatus(ExecutedTradeStatus status)
        => status is ExecutedTradeStatus.Failed or ExecutedTradeStatus.Rejected or ExecutedTradeStatus.ValidationFailed or ExecutedTradeStatus.CloseFailed;

    private static bool IsExcludedExecutionTimeframe(string? timeframe)
        => string.Equals(timeframe?.Trim(), "1m", StringComparison.OrdinalIgnoreCase);

    private async Task DeferRetryAsync(QueuedTradeExecution entry, TradeExecutionResult result, CancellationToken ct)
    {
        var retryPending = entry with
        {
            Status = TradeExecutionQueueStatus.Processing,
            FailureReason = result.FailureReason,
            ErrorDetails = result.ErrorDetails ?? result.Message,
            ForceMarketExecution = entry.ForceMarketExecution || result.RetryWithForceMarketExecution,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            ProcessedAtUtc = null
        };
        await _queueRepository.UpdateAsync(retryPending, ct);

        var retryDelay = ResolveRetryDelay(result);
        _logger.LogInformation(
            "[TradeExecutionQueue] Deferred retry for queue entry {QueueEntryId} signal {SignalId} by {DelayMs}ms (Reason={Reason}, ForceMarketExecution={ForceMarketExecution})",
            entry.QueueEntryId,
            entry.SignalId,
            retryDelay.TotalMilliseconds,
            result.FailureReason,
            retryPending.ForceMarketExecution);

        _ = ResumeDeferredRetryAsync(retryPending, retryDelay, ct);
    }

    private async Task ResumeDeferredRetryAsync(QueuedTradeExecution entry, TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            var requeued = entry with
            {
                Status = TradeExecutionQueueStatus.Queued,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await _queueRepository.UpdateAsync(requeued, ct);
            if (!ct.IsCancellationRequested)
                NotifyWorkAvailable();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TradeExecutionQueue] Failed to resume deferred retry for queue entry {QueueEntryId}", entry.QueueEntryId);
        }
    }

    private static TimeSpan ResolveRetryDelay(TradeExecutionResult result)
    {
        if (result.RetryWithForceMarketExecution)
            return ForceMarketRetryDelay;
        if (string.Equals(result.FailureReason, "MaxConcurrentOpenTrades", StringComparison.Ordinal))
            return CapacityRetryDelay;
        return DefaultRetryDelay;
    }

    private sealed record DispatchContext(
        int BrokerOpenTradeCount,
        int PendingSubmissionCount,
        int QueuedCount,
        int ProcessingCount,
        int CompletedCount,
        int FailedCount,
        int AvailableDispatchSlots);
}
