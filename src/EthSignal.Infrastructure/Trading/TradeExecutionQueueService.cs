using System.Text.Json;
using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using Microsoft.Extensions.Logging;

namespace EthSignal.Infrastructure.Trading;

public sealed class TradeExecutionQueueService : ITradeExecutionQueueService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly ITradeExecutionQueueRepository _queueRepository;
    private readonly IExecutedTradeRepository _executedTradeRepository;
    private readonly ITradeExecutionPolicy _policy;
    private readonly ITradeExecutionService _executionService;
    private readonly IAccountSnapshotService _accountSnapshotService;
    private readonly ILogger<TradeExecutionQueueService> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public TradeExecutionQueueService(
        ITradeExecutionQueueRepository queueRepository,
        IExecutedTradeRepository executedTradeRepository,
        ITradeExecutionPolicy policy,
        ITradeExecutionService executionService,
        IAccountSnapshotService accountSnapshotService,
        ILogger<TradeExecutionQueueService> logger,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _queueRepository = queueRepository;
        _executedTradeRepository = executedTradeRepository;
        _policy = policy;
        _executionService = executionService;
        _accountSnapshotService = accountSnapshotService;
        _logger = logger;
        _delayAsync = delayAsync ?? Task.Delay;
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
        _logger.LogInformation(
            "[TradeExecutionQueue] Queued {SourceType} signal {SignalId} as queue entry {QueueEntryId}",
            request.Candidate.SourceType,
            request.Candidate.SignalId,
            queueEntryId);

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

        var processed = 0;
        var queueConcurrentRequestLimit = Math.Clamp(settings.QueueConcurrentRequestLimit, 1, 3);
        var queueCooldown = TimeSpan.FromMilliseconds(Math.Clamp(settings.QueueCooldownMilliseconds, 0, 500));
        while (!ct.IsCancellationRequested)
        {
            var activeTrades = await GetActiveTradeCountAsync(settings, ct);
            var availableCapacity = settings.MaxConcurrentOpenTrades - activeTrades;
            if (availableCapacity <= 0)
            {
                _logger.LogInformation(
                    "[TradeExecutionQueue] Waiting for free capacity before processing queued trades ({ActiveTrades}/{MaxConcurrentOpenTrades})",
                    activeTrades,
                    settings.MaxConcurrentOpenTrades);
                break;
            }

            var batchSize = Math.Min(availableCapacity, queueConcurrentRequestLimit);
            var entries = new List<QueuedTradeExecution>(batchSize);
            for (var i = 0; i < batchSize; i++)
            {
                var entry = await _queueRepository.GetNextQueuedAsync(ct);
                if (entry == null)
                    break;

                var processingEntry = entry with
                {
                    Status = TradeExecutionQueueStatus.Processing,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
                await _queueRepository.UpdateAsync(processingEntry, ct);
                entries.Add(processingEntry);
            }

            if (entries.Count == 0)
                break;

            var results = await Task.WhenAll(entries.Select(entry => ProcessEntryAsync(entry, ct)));
            processed += results.Count(result => result.Status != TradeExecutionQueueStatus.Queued);

            if (entries.Count == queueConcurrentRequestLimit
                && queueCooldown > TimeSpan.Zero
                && await _queueRepository.HasQueuedAsync(ct))
            {
                _logger.LogInformation(
                    "[TradeExecutionQueue] Applied cooldown of {CooldownMs}ms after a full {BatchSize}-request queue burst",
                    queueCooldown.TotalMilliseconds,
                    entries.Count);
                await _delayAsync(queueCooldown, ct);
            }
        }

        return processed;
    }

    public async Task<TradeExecutionQueueSnapshot> GetSnapshotAsync(int limit = 50, CancellationToken ct = default)
    {
        var settings = _policy.GetSettings();
        var serverTimeUtc = DateTimeOffset.UtcNow;
        var account = await _accountSnapshotService.GetLatestAsync(ct);
        var activeTradeCount = await _executedTradeRepository.GetActiveExecutedTradeCountAsync(new ExecutedTradeQuery
        {
            AccountId = account.AccountId,
            AccountName = account.AccountName,
            IsDemo = settings.DemoOnly ? true : null
        }, ct);
        var counts = await _queueRepository.GetStatusCountsAsync(ct);
        var entries = await _queueRepository.GetActiveEntriesAsync(limit, ct);

        return new TradeExecutionQueueSnapshot
        {
            ServerTimeUtc = serverTimeUtc,
            ActiveTradeCount = activeTradeCount,
            MaxConcurrentOpenTrades = settings.MaxConcurrentOpenTrades,
            QueueConcurrentRequestLimit = Math.Clamp(settings.QueueConcurrentRequestLimit, 1, 3),
            QueueCooldownMilliseconds = Math.Clamp(settings.QueueCooldownMilliseconds, 0, 500),
            QueuedCount = counts.QueuedCount,
            ProcessingCount = counts.ProcessingCount,
            CompletedCount = counts.CompletedCount,
            FailedCount = counts.FailedCount,
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

    private async Task<QueuedTradeExecution> ProcessEntryAsync(QueuedTradeExecution entry, CancellationToken ct)
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
            return duplicate;
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
            return invalid;
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
            return invalid;
        }

        var result = await _executionService.ExecuteAsync(new TradeExecutionRequest
        {
            Candidate = candidate,
            RequestedBy = entry.RequestedBy,
            RequestedSize = entry.RequestedSize,
            ForceMarketExecution = entry.ForceMarketExecution
        }, ct);

        if (!result.Success && string.Equals(result.FailureReason, "MaxConcurrentOpenTrades", StringComparison.Ordinal))
        {
            var requeued = entry with
            {
                Status = TradeExecutionQueueStatus.Queued,
                FailureReason = result.FailureReason,
                ErrorDetails = result.Message,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await _queueRepository.UpdateAsync(requeued, ct);
            return requeued;
        }

        var completedStatus = result.Success ? TradeExecutionQueueStatus.Completed : TradeExecutionQueueStatus.Failed;
        var processed = entry with
        {
            Status = completedStatus,
            ExecutedTradeId = result.ExecutedTradeId,
            FailureReason = result.FailureReason,
            ErrorDetails = result.ErrorDetails ?? result.Message,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            ProcessedAtUtc = DateTimeOffset.UtcNow
        };
        await _queueRepository.UpdateAsync(processed, ct);

        _logger.LogInformation(
            "[TradeExecutionQueue] Processed queue entry {QueueEntryId} for {SourceType} signal {SignalId} -> {Status}",
            entry.QueueEntryId,
            entry.SourceType,
            entry.SignalId,
            processed.Status);

        return processed;
    }

    private async Task<int> GetActiveTradeCountAsync(TradeExecutionPolicySettings settings, CancellationToken ct)
    {
        var account = await _accountSnapshotService.GetLatestAsync(ct);
        return await _executedTradeRepository.GetActiveExecutedTradeCountAsync(new ExecutedTradeQuery
        {
            AccountId = account.AccountId,
            AccountName = account.AccountName,
            IsDemo = settings.DemoOnly ? true : null
        }, ct);
    }

    private static bool IsRetryableTerminalStatus(ExecutedTradeStatus status)
        => status is ExecutedTradeStatus.Failed or ExecutedTradeStatus.Rejected or ExecutedTradeStatus.ValidationFailed or ExecutedTradeStatus.CloseFailed;

    private static bool IsExcludedExecutionTimeframe(string? timeframe)
        => string.Equals(timeframe?.Trim(), "1m", StringComparison.OrdinalIgnoreCase);
}
