using System.Diagnostics;
using EthSignal.Domain.Models;
using EthSignal.Domain.Validation;
using EthSignal.Infrastructure.Apis;
using EthSignal.Infrastructure.Db;
using Microsoft.Extensions.Logging;

namespace EthSignal.Infrastructure.Engine;

public sealed class BackfillService
{
    private const int ChunkHours = 12;

    private readonly ICapitalClient _api;
    private readonly ICandleRepository _repo;
    private readonly IAuditRepository _audit;
    private readonly IIndicatorRepository _indicatorRepo;
    private readonly IRegimeRepository _regimeRepo;
    private readonly IParameterProvider _paramProvider;
    private readonly ILogger<BackfillService> _logger;

    public BackfillService(ICapitalClient api, ICandleRepository repo, IAuditRepository audit,
        IIndicatorRepository indicatorRepo, IRegimeRepository regimeRepo,
        IParameterProvider paramProvider, ILogger<BackfillService> logger)
    {
        _api = api;
        _repo = repo;
        _audit = audit;
        _indicatorRepo = indicatorRepo;
        _regimeRepo = regimeRepo;
        _paramProvider = paramProvider;
        _logger = logger;
    }

    public async Task BackfillAsync(string symbol, string epic, int days, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        // REQ-NS-003: Align to last fully closed minute so the current open minute is never counted as a gap
        var toUtc = Timeframe.M1.Floor(DateTimeOffset.UtcNow);
        var earliest = toUtc.AddDays(-days);

        var latestClosed = await _repo.GetLatestClosedTimeAsync(Timeframe.M1, symbol, ct);
        var fromUtc = latestClosed?.AddMinutes(1) ?? earliest;

        if (fromUtc >= toUtc)
        {
            _logger.LogInformation("Backfill: already up to date.");
            return;
        }

        var totalChunks = (int)Math.Ceiling((toUtc - fromUtc).TotalHours / ChunkHours);
        _logger.LogInformation("Backfilling from {From:u} to {To:u} ({Chunks} chunks)...",
            fromUtc, toUtc, totalChunks);

        var allCandles = new List<RichCandle>();
        var chunkStart = fromUtc;
        var chunk = 0;
        var validationErrors = 0;

        while (chunkStart < toUtc)
        {
            var chunkEnd = chunkStart.AddHours(ChunkHours);
            if (chunkEnd > toUtc) chunkEnd = toUtc;
            chunk++;

            _logger.LogDebug("Fetching chunk {Chunk}/{Total}...", chunk, totalChunks);
            try
            {
                var batch = await _api.GetCandlesAsync(epic, "MINUTE", chunkStart, chunkEnd, 1000, ct);
                _logger.LogDebug("Chunk {Chunk}/{Total} returned {Count} candles", chunk, totalChunks, batch.Count);
                allCandles.AddRange(batch);
            }
            catch (Exception ex) when (ex.Message.Contains("error.prices.not-found", StringComparison.OrdinalIgnoreCase))
            {
                // Market closed or holiday — no data for this window, skip silently
                _logger.LogDebug("Chunk {Chunk}/{Total} [{From:u}–{To:u}]: no price data (market closed/holiday) — skipping",
                    chunk, totalChunks, chunkStart, chunkEnd);
            }
            chunkStart = chunkEnd;
        }

        // Deduplicate
        allCandles = allCandles
            .GroupBy(c => c.OpenTime)
            .Select(g => g.First())
            .OrderBy(c => c.OpenTime)
            .ToList();

        // Validate
        foreach (var c in allCandles)
        {
            var result = CandleValidator.Validate(c);
            if (!result.IsValid)
            {
                validationErrors++;
                _logger.LogWarning("Candle validation failed at {Time}: {Errors}",
                    c.OpenTime, string.Join("; ", result.Errors));
            }
        }

        _logger.LogInformation("Fetched {Count} 1m candles (validation errors: {Errors})", allCandles.Count, validationErrors);

        // Store 1m
        var inserted = await _repo.BulkUpsertAsync(Timeframe.M1, symbol, allCandles, ct);
        _logger.LogInformation("Stored 1m: {Inserted} rows", inserted);

        // P1-04 FIX: Aggregate 5m/15m with completeness checks
        // Use repository history (not just current batch) for complete HTF alignment
        foreach (var tf in Timeframe.All)
        {
            if (tf.Minutes == 1) continue;

            var historyStart = tf.Floor(fromUtc).AddMinutes(-tf.Minutes); // include previous bucket for alignment
            var m1History = await _repo.GetClosedCandlesInRangeAsync(Timeframe.M1, symbol, historyStart, toUtc, ct);
            if (m1History.Count == 0) continue;

            var aggregated = CandleAggregator.Aggregate(m1History.ToList(), tf);
            var completeCandles = new List<RichCandle>();
            var partialCount = 0;

            foreach (var agg in aggregated)
            {
                var bucketStart = agg.OpenTime;
                var bucketEnd = bucketStart.Add(tf.Duration);
                var m1InBucket = m1History.Count(c => c.OpenTime >= bucketStart && c.OpenTime < bucketEnd);

                if (m1InBucket >= tf.Minutes)
                {
                    completeCandles.Add(agg with { IsClosed = true });
                }
                else if (bucketEnd <= toUtc)
                {
                    completeCandles.Add(agg with { IsClosed = false });
                    partialCount++;
                }
            }

            if (completeCandles.Count > 0)
            {
                var aggInserted = await _repo.BulkUpsertAsync(tf, symbol, completeCandles, ct);
                _logger.LogInformation("Stored {Tf}: {Inserted} rows ({Complete} complete, {Partial} partial)",
                    tf.Name, aggInserted,
                    completeCandles.Count(c => c.IsClosed),
                    partialCount);
            }
        }

        // C-04: Only close candles whose bucket end is before the backfill boundary
        // (partial candles that span beyond toUtc are left open for live tick processor)
        await _repo.CloseOpenCandlesBeforeAsync(symbol, toUtc, ct);

        // Phase 2: Compute indicators for 5m and 15m closed candles
        _logger.LogInformation("Computing indicators...");
        var p = _paramProvider.GetActive();
        foreach (var tf in Timeframe.All)
        {
            if (tf.Minutes == 1) continue;
            var closed = await _repo.GetClosedCandlesAsync(tf, symbol, 2000, ct);
            if (closed.Count == 0) continue;
            var snapshots = IndicatorEngine.ComputeAll(symbol, tf.Name, closed, p);
            await _indicatorRepo.BulkUpsertAsync(snapshots, ct);
            _logger.LogInformation("Indicators {Tf}: {Count} snapshots computed", tf.Name, snapshots.Count);
        }

        // P3-02 FIX: Persist full regime history for every eligible 15m bar
        _logger.LogInformation("Classifying full 15m regime history...");
        {
            var closed15m = await _repo.GetClosedCandlesAsync(Timeframe.M15, symbol, 2000, ct);
            if (closed15m.Count >= IndicatorEngine.WarmUpPeriod)
            {
                var ind15m = IndicatorEngine.ComputeAll(symbol, Timeframe.M15.Name, closed15m, p);
                var finalSnapshots = ind15m.Where(s => !s.IsProvisional).ToList();

                int regimeCount = 0;
                for (int i = 4; i < finalSnapshots.Count; i++)
                {
                    var windowSnapshots = finalSnapshots.Take(i + 1).ToList();
                    var regime = RegimeAnalyzer.Classify(symbol, windowSnapshots, p);
                    await _regimeRepo.UpsertAsync(regime, ct);
                    regimeCount++;
                }

                _logger.LogInformation("Regime history: {Count} snapshots classified", regimeCount);
            }
        }

        // Gap detection on 1m
        var actualTimes = await _repo.GetCandleTimesAsync(
            Timeframe.M1, symbol, earliest, toUtc, ct);
        var gaps = GapDetector.DetectGaps(symbol, Timeframe.M1, earliest, toUtc, actualTimes, gapSource: "BACKFILL");
        if (gaps.Count > 0)
        {
            _logger.LogWarning("Detected {Count} gaps in 1m candles.", gaps.Count);
            await _audit.InsertGapsAsync(gaps, ct);
        }

        sw.Stop();

        await _audit.InsertAuditAsync(new IngestionAuditEntry(
            Operation: "backfill",
            Symbol: symbol,
            TimeframeName: "1m",
            PeriodFrom: fromUtc,
            PeriodTo: toUtc,
            CandlesFetched: allCandles.Count,
            CandlesInserted: inserted,
            CandlesUpdated: 0,
            DuplicatesSkipped: 0,
            ValidationErrors: validationErrors,
            Duration: sw.Elapsed,
            Success: true,
            ErrorMessage: null,
            CreatedAtUtc: DateTimeOffset.UtcNow), ct);

        _logger.LogInformation("Backfill complete: {Count} candles in {Elapsed}.",
            allCandles.Count, sw.Elapsed);
    }
}
