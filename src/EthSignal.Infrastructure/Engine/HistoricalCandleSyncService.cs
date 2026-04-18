using System.Diagnostics;
using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Apis;
using EthSignal.Infrastructure.Db;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EthSignal.Infrastructure.Engine;

/// <summary>
/// Startup-time historical candle sync coordinator.
///
/// For each timeframe under <see cref="Timeframe.All"/> this service:
/// 1. detects sync mode (EMPTY_BOOTSTRAP / OFFLINE_GAP_RECOVERY / NOOP),
/// 2. plans the closed-candle range to fetch (always strictly less than the
///    current open-candle boundary),
/// 3. fetches the range in chunked Capital API calls with rate-limit-aware
///    retry,
/// 4. upserts the candles via <see cref="ICandleRepository.BulkUpsertAsync"/>
///    so writes are idempotent across reruns,
/// 5. persists per-timeframe progress in <c>candle_sync_status</c> after
///    every chunk so dashboard/health surfaces stay in sync with reality.
///
/// Replaces startup usage of <see cref="BackfillService"/> for portal
/// completeness. The currently open candle is owned by
/// <see cref="LiveTickProcessor"/> and is never written here.
/// </summary>
public sealed class HistoricalCandleSyncService
{
    // Highest timeframes first so the portal becomes useful quickly while
    // 1m / 5m are still being filled.
    private static readonly Timeframe[] SyncOrder =
    [
        Timeframe.H4, Timeframe.H1, Timeframe.M30, Timeframe.M15, Timeframe.M5, Timeframe.M1
    ];

    private readonly ICapitalClient _api;
    private readonly ICandleRepository _candleRepo;
    private readonly ICandleSyncRepository _syncRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly IConfiguration _config;
    private readonly ILogger<HistoricalCandleSyncService> _logger;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public HistoricalCandleSyncService(
        ICapitalClient api,
        ICandleRepository candleRepo,
        ICandleSyncRepository syncRepo,
        IAuditRepository auditRepo,
        IConfiguration config,
        ILogger<HistoricalCandleSyncService> logger,
        Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _api = api;
        _candleRepo = candleRepo;
        _syncRepo = syncRepo;
        _auditRepo = auditRepo;
        _config = config;
        _logger = logger;
        _now = clock ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
    }

    /// <summary>
    /// Returns true when historical sync is enabled. Decoupled from
    /// HighFreqTicks:UiPriceOnly so the portal still gets candle history
    /// when live ticks come from the inspected UI price stream.
    /// </summary>
    public bool IsEnabled =>
        ReadBool("CapitalApi:HistoricalSyncEnabled", defaultValue: true);

    public async Task<StartupCandleSyncSummary> RunAsync(
        string symbol, string epic, CancellationToken ct)
    {
        var startupDays = ReadInt("CapitalApi:StartupHistoricalDays", defaultValue: 30);
        var chunkCandles = ReadInt("CapitalApi:HistoricalSyncChunkCandles", defaultValue: 400);
        var chunkDelayMs = ReadInt("CapitalApi:HistoricalSyncChunkDelayMs", defaultValue: 1200);
        var maxRetries = ReadInt("CapitalApi:HistoricalSyncMaxRetries", defaultValue: 6);
        var retryBaseMs = ReadInt("CapitalApi:HistoricalSyncRetryBaseDelayMs", defaultValue: 2000);

        var startedAt = _now();
        var sw = Stopwatch.StartNew();

        _logger.LogInformation(
            "Startup candle sync starting for {Symbol} ({TfCount} timeframes, {Days}d, chunk={ChunkSize})",
            symbol, SyncOrder.Length, startupDays, chunkCandles);

        var results = new List<TimeframeSyncResult>(SyncOrder.Length);

        foreach (var tf in SyncOrder)
        {
            ct.ThrowIfCancellationRequested();

            TimeframeSyncPlan plan;
            try
            {
                plan = await PlanAsync(tf, symbol, startupDays, chunkCandles, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to plan {Tf} sync — marking failed", tf.Name);
                var failedRow = MakeStatusRow(symbol, tf,
                    status: TimeframeSyncStatus.Failed,
                    mode: TimeframeSyncMode.EmptyBootstrap,
                    isEmpty: false,
                    plan: null,
                    chunksCompleted: 0,
                    lastSyncedCandle: null,
                    runStartedAt: startedAt,
                    runFinishedAt: _now(),
                    lastSuccessAt: null,
                    error: ex.Message);
                await _syncRepo.UpsertAsync(failedRow, ct);
                results.Add(new TimeframeSyncResult
                {
                    Tf = tf,
                    Mode = TimeframeSyncMode.EmptyBootstrap,
                    Status = TimeframeSyncStatus.Failed,
                    Elapsed = TimeSpan.Zero,
                    Error = ex.Message
                });
                continue;
            }

            _logger.LogInformation(
                "Timeframe sync planned: {Tf} mode={Mode} from={From:u} to={To:u} lastExisting={LastExisting:u} offlineSec={OfflineSec} chunks={Chunks}",
                tf.Name, plan.Mode, plan.SyncFromUtc, plan.SyncToUtc,
                plan.LastExistingClosed, (long)plan.OfflineDuration.TotalSeconds, plan.ChunksTotal);

            var result = await ExecutePlanAsync(plan, symbol, epic, startedAt,
                chunkDelayMs, maxRetries, retryBaseMs, ct);
            results.Add(result);
        }

        sw.Stop();
        var ready = results.Count(r => r.Status == TimeframeSyncStatus.Ready);
        var failed = results.Count(r => r.Status == TimeframeSyncStatus.Failed);
        var running = results.Count(r => r.Status == TimeframeSyncStatus.Running);
        var noop = results.Count(r => r.Mode == TimeframeSyncMode.Noop);
        var overallStatus = failed > 0
            ? TimeframeSyncStatus.Failed
            : ready == results.Count
                ? TimeframeSyncStatus.Ready
                : TimeframeSyncStatus.Running;

        var summary = new StartupCandleSyncSummary
        {
            Symbol = symbol,
            Status = overallStatus,
            TotalTimeframes = results.Count,
            ReadyTimeframes = ready,
            FailedTimeframes = failed,
            RunningTimeframes = running,
            NoopTimeframes = noop,
            Elapsed = sw.Elapsed,
            StartedAtUtc = startedAt,
            FinishedAtUtc = _now(),
            Timeframes = results
        };

        if (failed > 0)
        {
            _logger.LogError(
                "Startup candle sync FAILED for {Symbol}: ready={Ready}/{Total} failed={Failed} elapsed={Elapsed}",
                symbol, ready, results.Count, failed, sw.Elapsed);
        }
        else
        {
            _logger.LogInformation(
                "Startup candle sync completed: {Symbol} ready={Ready}/{Total} (noop={Noop}) elapsed={Elapsed}",
                symbol, ready, results.Count, noop, sw.Elapsed);
        }

        return summary;
    }

    /// <summary>
    /// Builds the per-timeframe plan. Pure function over repository state plus
    /// the injected clock so unit tests can hit it without real DB or HTTP.
    /// </summary>
    public async Task<TimeframeSyncPlan> PlanAsync(
        Timeframe tf, string symbol, int startupDays, int chunkCandles, CancellationToken ct)
    {
        var nowUtc = _now();
        var currentOpenBoundary = tf.Floor(nowUtc);
        var bootstrapStart = tf.Floor(currentOpenBoundary.AddDays(-startupDays));

        var count = await _candleRepo.CountCandlesAsync(tf, symbol, ct);
        var isEmpty = count == 0;

        DateTimeOffset? earliestClosed = null;
        DateTimeOffset? latestClosed = null;
        if (!isEmpty)
        {
            earliestClosed = await _candleRepo.GetEarliestClosedTimeAsync(tf, symbol, ct);
            latestClosed = await _candleRepo.GetLatestClosedTimeAsync(tf, symbol, ct);
        }

        DateTimeOffset syncFrom;
        DateTimeOffset syncTo = currentOpenBoundary;
        string mode;
        TimeSpan offline = TimeSpan.Zero;

        if (isEmpty || latestClosed is null)
        {
            mode = TimeframeSyncMode.EmptyBootstrap;
            syncFrom = bootstrapStart;
        }
        else if (earliestClosed is null || earliestClosed > bootstrapStart)
        {
            // A partially populated table is not considered "bootstrapped" yet.
            // Rebuild the requested startup horizon so older candles like 1m are
            // filled in instead of recovering only the forward-most gap.
            mode = TimeframeSyncMode.EmptyBootstrap;
            syncFrom = bootstrapStart;
            offline = ComputeOfflineDuration(tf, latestClosed.Value, currentOpenBoundary);
        }
        else
        {
            var expectedNextOpen = latestClosed.Value + tf.Duration;
            if (expectedNextOpen >= currentOpenBoundary)
            {
                mode = TimeframeSyncMode.Noop;
                syncFrom = currentOpenBoundary;
            }
            else
            {
                mode = TimeframeSyncMode.OfflineGapRecovery;
                syncFrom = expectedNextOpen;
                offline = currentOpenBoundary - expectedNextOpen;
            }
        }

        var chunksTotal = mode == TimeframeSyncMode.Noop
            ? 0
            : ComputeChunkCount(syncFrom, syncTo, tf, chunkCandles);

        return new TimeframeSyncPlan
        {
            Tf = tf,
            Mode = mode,
            IsTableEmpty = isEmpty,
            LastExistingClosed = latestClosed,
            SyncFromUtc = syncFrom,
            SyncToUtc = syncTo,
            ChunkSizeCandles = chunkCandles,
            ChunksTotal = chunksTotal,
            OfflineDuration = offline
        };
    }

    private static TimeSpan ComputeOfflineDuration(
        Timeframe tf, DateTimeOffset latestClosed, DateTimeOffset currentOpenBoundary)
    {
        var expectedNextOpen = latestClosed + tf.Duration;
        return expectedNextOpen >= currentOpenBoundary
            ? TimeSpan.Zero
            : currentOpenBoundary - expectedNextOpen;
    }

    /// <summary>
    /// Splits a [from, to) range into the smallest number of equal-size chunks
    /// each holding at most <paramref name="chunkCandles"/> candles for the
    /// given timeframe. Exposed for unit tests.
    /// </summary>
    public static int ComputeChunkCount(
        DateTimeOffset from, DateTimeOffset to, Timeframe tf, int chunkCandles)
    {
        if (to <= from) return 0;
        if (chunkCandles <= 0) chunkCandles = 1;
        var totalCandles = (long)Math.Ceiling((to - from).TotalMinutes / tf.Minutes);
        return (int)Math.Max(1, (totalCandles + chunkCandles - 1) / chunkCandles);
    }

    private async Task<TimeframeSyncResult> ExecutePlanAsync(
        TimeframeSyncPlan plan, string symbol, string epic, DateTimeOffset startedAt,
        int chunkDelayMs, int maxRetries, int retryBaseMs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Persist the plan even for NOOP timeframes so operators can see we checked.
        var initialRow = MakeStatusRow(symbol, plan.Tf,
            status: plan.Mode == TimeframeSyncMode.Noop
                ? TimeframeSyncStatus.Ready
                : TimeframeSyncStatus.Running,
            mode: plan.Mode,
            isEmpty: plan.IsTableEmpty,
            plan: plan,
            chunksCompleted: 0,
            lastSyncedCandle: plan.LastExistingClosed,
            runStartedAt: startedAt,
            runFinishedAt: plan.Mode == TimeframeSyncMode.Noop ? _now() : null,
            lastSuccessAt: plan.Mode == TimeframeSyncMode.Noop ? _now() : null,
            error: null);
        await _syncRepo.UpsertAsync(initialRow, ct);

        if (plan.Mode == TimeframeSyncMode.Noop)
        {
            _logger.LogInformation("Timeframe {Tf} already up to date — NOOP", plan.Tf.Name);
            return new TimeframeSyncResult
            {
                Tf = plan.Tf,
                Mode = plan.Mode,
                Status = TimeframeSyncStatus.Ready,
                ChunksCompleted = 0,
                ChunksTotal = 0,
                CandlesFetched = 0,
                CandlesUpserted = 0,
                LastSyncedCandleUtc = plan.LastExistingClosed,
                Elapsed = sw.Elapsed
            };
        }

        var chunkDuration = TimeSpan.FromMinutes((long)plan.ChunkSizeCandles * plan.Tf.Minutes);
        var chunkStart = plan.SyncFromUtc;
        var chunksCompleted = 0;
        var totalFetched = 0;
        var totalUpserted = 0;
        DateTimeOffset? lastSyncedCandle = plan.LastExistingClosed;

        while (chunkStart < plan.SyncToUtc)
        {
            ct.ThrowIfCancellationRequested();

            var chunkEnd = chunkStart + chunkDuration;
            if (chunkEnd > plan.SyncToUtc) chunkEnd = plan.SyncToUtc;
            var chunkIndex = chunksCompleted + 1;

            _logger.LogInformation(
                "Timeframe sync chunk starting: {Tf} {Index}/{Total} [{From:u}–{To:u}]",
                plan.Tf.Name, chunkIndex, plan.ChunksTotal, chunkStart, chunkEnd);

            List<RichCandle> batch;
            try
            {
                batch = await FetchWithRetryAsync(epic, plan.Tf, chunkStart, chunkEnd,
                    plan.ChunkSizeCandles, maxRetries, retryBaseMs, plan.Tf.Name, chunkIndex, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex,
                    "Timeframe sync failed: {Tf} chunk {Index}/{Total}: {Error}",
                    plan.Tf.Name, chunkIndex, plan.ChunksTotal, ex.Message);

                var failedRow = MakeStatusRow(symbol, plan.Tf,
                    status: TimeframeSyncStatus.Failed,
                    mode: plan.Mode,
                    isEmpty: plan.IsTableEmpty,
                    plan: plan,
                    chunksCompleted: chunksCompleted,
                    lastSyncedCandle: lastSyncedCandle,
                    runStartedAt: startedAt,
                    runFinishedAt: _now(),
                    lastSuccessAt: null,
                    error: ex.Message);
                await _syncRepo.UpsertAsync(failedRow, ct);

                await SafeAuditAsync(symbol, plan.Tf.Name, plan.SyncFromUtc, plan.SyncToUtc,
                    totalFetched, totalUpserted, sw.Elapsed, success: false, error: ex.Message, ct);

                return new TimeframeSyncResult
                {
                    Tf = plan.Tf,
                    Mode = plan.Mode,
                    Status = TimeframeSyncStatus.Failed,
                    ChunksCompleted = chunksCompleted,
                    ChunksTotal = plan.ChunksTotal,
                    CandlesFetched = totalFetched,
                    CandlesUpserted = totalUpserted,
                    LastSyncedCandleUtc = lastSyncedCandle,
                    Elapsed = sw.Elapsed,
                    Error = ex.Message
                };
            }

            // Closed candles only — startup sync never owns the open candle.
            var closedAligned = batch
                .Where(c => c.OpenTime >= chunkStart && c.OpenTime < plan.SyncToUtc)
                .Select(c => c with { IsClosed = true })
                .ToList();

            var upserted = closedAligned.Count > 0
                ? await _candleRepo.BulkUpsertAsync(plan.Tf, symbol, closedAligned, ct)
                : 0;

            totalFetched += batch.Count;
            totalUpserted += upserted;
            chunksCompleted++;
            if (closedAligned.Count > 0)
                lastSyncedCandle = closedAligned[^1].OpenTime;

            _logger.LogInformation(
                "Timeframe sync chunk completed: {Tf} {Index}/{Total} fetched={Fetched} upserted={Upserted}",
                plan.Tf.Name, chunkIndex, plan.ChunksTotal, batch.Count, upserted);

            // Persist progress after every chunk so a crash leaves the dashboard
            // pointing at the right last-completed chunk.
            var progressRow = MakeStatusRow(symbol, plan.Tf,
                status: TimeframeSyncStatus.Running,
                mode: plan.Mode,
                isEmpty: plan.IsTableEmpty,
                plan: plan,
                chunksCompleted: chunksCompleted,
                lastSyncedCandle: lastSyncedCandle,
                runStartedAt: startedAt,
                runFinishedAt: null,
                lastSuccessAt: null,
                error: null);
            await _syncRepo.UpsertAsync(progressRow, ct);

            chunkStart = chunkEnd;

            if (chunkStart < plan.SyncToUtc && chunkDelayMs > 0)
                await _delay(TimeSpan.FromMilliseconds(chunkDelayMs), ct);
        }

        sw.Stop();

        var finishedRow = MakeStatusRow(symbol, plan.Tf,
            status: TimeframeSyncStatus.Ready,
            mode: plan.Mode,
            isEmpty: plan.IsTableEmpty,
            plan: plan,
            chunksCompleted: chunksCompleted,
            lastSyncedCandle: lastSyncedCandle,
            runStartedAt: startedAt,
            runFinishedAt: _now(),
            lastSuccessAt: _now(),
            error: null);
        await _syncRepo.UpsertAsync(finishedRow, ct);

        await SafeAuditAsync(symbol, plan.Tf.Name, plan.SyncFromUtc, plan.SyncToUtc,
            totalFetched, totalUpserted, sw.Elapsed, success: true, error: null, ct);

        _logger.LogInformation(
            "Timeframe sync completed: {Tf} mode={Mode} fetched={Fetched} upserted={Upserted} offlineSec={OfflineSec} elapsed={Elapsed}",
            plan.Tf.Name, plan.Mode, totalFetched, totalUpserted,
            (long)plan.OfflineDuration.TotalSeconds, sw.Elapsed);

        return new TimeframeSyncResult
        {
            Tf = plan.Tf,
            Mode = plan.Mode,
            Status = TimeframeSyncStatus.Ready,
            ChunksCompleted = chunksCompleted,
            ChunksTotal = plan.ChunksTotal,
            CandlesFetched = totalFetched,
            CandlesUpserted = totalUpserted,
            LastSyncedCandleUtc = lastSyncedCandle,
            Elapsed = sw.Elapsed
        };
    }

    private async Task<List<RichCandle>> FetchWithRetryAsync(
        string epic, Timeframe tf, DateTimeOffset from, DateTimeOffset to, int max,
        int maxRetries, int retryBaseMs, string tfName, int chunkIndex, CancellationToken ct)
    {
        var attempt = 0;
        var delay = TimeSpan.FromMilliseconds(retryBaseMs);

        while (true)
        {
            attempt++;
            try
            {
                return await _api.GetCandlesAsync(epic, tf.ApiResolution, from, to, max, ct);
            }
            catch (Exception ex) when (
                ex.Message.Contains("error.prices.not-found", StringComparison.OrdinalIgnoreCase))
            {
                // Market closed / holiday — same shape as BackfillService treats it.
                _logger.LogDebug(
                    "Timeframe sync chunk no data: {Tf} chunk {Index} [{From:u}–{To:u}]",
                    tfName, chunkIndex, from, to);
                return [];
            }
            catch (Exception ex) when (IsTransient(ex) && attempt <= maxRetries)
            {
                _logger.LogWarning(
                    "Timeframe sync rate limited / transient: {Tf} chunk {Index} attempt={Attempt}/{Max} delay={Delay}ms err={Error}",
                    tfName, chunkIndex, attempt, maxRetries, (long)delay.TotalMilliseconds, ex.Message);
                await _delay(delay, ct);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 60_000));
            }
        }
    }

    private static bool IsTransient(Exception ex)
    {
        if (ex is HttpRequestException) return true;
        if (ex is TaskCanceledException) return true;
        var msg = ex.Message ?? "";
        return msg.Contains("429", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("500", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("502", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("503", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("504", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("rate", StringComparison.OrdinalIgnoreCase);
    }

    private bool ReadBool(string key, bool defaultValue)
    {
        var raw = _config[key];
        return bool.TryParse(raw, out var parsed) ? parsed : defaultValue;
    }

    private int ReadInt(string key, int defaultValue)
    {
        var raw = _config[key];
        return int.TryParse(raw, out var parsed) ? parsed : defaultValue;
    }

    private static CandleSyncStatusRow MakeStatusRow(
        string symbol, Timeframe tf,
        string status, string mode, bool isEmpty, TimeframeSyncPlan? plan,
        int chunksCompleted, DateTimeOffset? lastSyncedCandle,
        DateTimeOffset? runStartedAt, DateTimeOffset? runFinishedAt,
        DateTimeOffset? lastSuccessAt, string? error)
    {
        return new CandleSyncStatusRow(
            Symbol: symbol,
            Timeframe: tf.Name,
            Status: status,
            SyncMode: mode,
            IsTableEmpty: isEmpty,
            RequestedFromUtc: plan?.SyncFromUtc,
            RequestedToUtc: plan?.SyncToUtc,
            LastExistingCandleUtc: plan?.LastExistingClosed,
            LastSyncedCandleUtc: lastSyncedCandle,
            OfflineDurationSec: (long)(plan?.OfflineDuration.TotalSeconds ?? 0),
            ChunkSizeCandles: plan?.ChunkSizeCandles ?? 0,
            ChunksTotal: plan?.ChunksTotal ?? 0,
            ChunksCompleted: chunksCompleted,
            LastRunStartedAtUtc: runStartedAt,
            LastRunFinishedAtUtc: runFinishedAt,
            LastSuccessAtUtc: lastSuccessAt,
            LastError: error);
    }

    private async Task SafeAuditAsync(
        string symbol, string tfName, DateTimeOffset from, DateTimeOffset to,
        int fetched, int upserted, TimeSpan elapsed, bool success, string? error,
        CancellationToken ct)
    {
        try
        {
            await _auditRepo.InsertAuditAsync(new IngestionAuditEntry(
                Operation: "startup_candle_sync",
                Symbol: symbol,
                TimeframeName: tfName,
                PeriodFrom: from,
                PeriodTo: to,
                CandlesFetched: fetched,
                CandlesInserted: upserted,
                CandlesUpdated: 0,
                DuplicatesSkipped: 0,
                ValidationErrors: 0,
                Duration: elapsed,
                Success: success,
                ErrorMessage: error,
                CreatedAtUtc: _now()), ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Audit insert failed for startup candle sync (non-fatal)");
        }
    }
}
