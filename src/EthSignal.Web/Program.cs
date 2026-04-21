using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Apis;
using EthSignal.Infrastructure.Db;
using EthSignal.Infrastructure.Engine;
using EthSignal.Infrastructure.Engine.ML;
using EthSignal.Infrastructure;
using EthSignal.Infrastructure.Notifications;
using EthSignal.Infrastructure.Trading;
using EthSignal.Web.BackgroundServices;
using Npgsql;
using Serilog;
using Serilog.Events;

// ─── Serilog bootstrap ─────────────────────────────────
var logDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "logs");
logDir = Path.GetFullPath(logDir); // resolve to repo root /logs
var logFileName = "ethsignal-";

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: Path.Combine(logDir, $"{logFileName}.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        fileSizeLimitBytes: 50 * 1024 * 1024, // 50 MB per file
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}{NewLine}  {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    builder.Configuration.AddEnvironmentVariables();

    static string ResolveWorkspaceRoot(string startPath)
    {
        // T3-18: Allow explicit override via environment variable for deployed environments
        // where the .sln file may not be present (published output, Docker, etc.).
        var envOverride = Environment.GetEnvironmentVariable("WORKSPACE_ROOT");
        if (!string.IsNullOrWhiteSpace(envOverride) && Directory.Exists(envOverride))
        {
            Log.Information("Workspace root from WORKSPACE_ROOT env: {Root}", envOverride);
            return Path.GetFullPath(envOverride);
        }

        // Walk up looking for .sln file (development layout)
        var dir = new DirectoryInfo(startPath);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return dir.FullName;
            dir = dir.Parent;
        }

        // Fallback: use the content root. Log a warning so operators know
        // that relative paths (ML scripts, user data dir) may not resolve correctly.
        Log.Warning(
            "Could not find workspace root by .sln lookup from '{StartPath}'. " +
            "Falling back to content root. Set WORKSPACE_ROOT environment variable " +
            "if ML scripts or Playwright UserDataDir are not found.",
            startPath);
        return Path.GetFullPath(startPath);
    }

    static string? ResolveConfigPath(string? configuredPath, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return null;
        if (Path.IsPathRooted(configuredPath))
            return Path.GetFullPath(configuredPath);
        return Path.GetFullPath(Path.Combine(workspaceRoot, configuredPath));
    }

    static string NormalizeMlPredictionScope(string? scope, string defaultScope = "linked")
    {
        var normalizedDefault = defaultScope.Trim().ToLowerInvariant();
        return scope?.Trim().ToLowerInvariant() switch
        {
            "all" => "all",
            "actionable" => "actionable",
            "linked" => "linked",
            _ => normalizedDefault
        };
    }

    static bool IsLoopbackRequest(HttpContext ctx)
    {
        var remoteIp = ctx.Connection.RemoteIpAddress;
        return remoteIp == null || System.Net.IPAddress.IsLoopback(remoteIp);
    }

    static IResult? RejectIfNotLoopback(HttpContext ctx)
        => IsLoopbackRequest(ctx)
            ? null
            : Results.StatusCode(StatusCodes.Status403Forbidden);

    var workspaceRoot = ResolveWorkspaceRoot(builder.Environment.ContentRootPath);
    var uiPriceOnly = builder.Configuration.GetValue<bool>("HighFreqTicks:UiPriceOnly", defaultValue: true);
    var historicalSyncEnabled = builder.Configuration.GetValue("CapitalApi:HistoricalSyncEnabled", defaultValue: true);

    // Serialize enums as strings ("BUY", "BULLISH") instead of integers (1, 2)
    builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
    {
        options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

    var capitalSection = builder.Configuration.GetSection("CapitalApi");
    var baseUrl = builder.Configuration["CAPITAL_BASE_URL"]
        ?? capitalSection["BaseUrl"]
        ?? throw new InvalidOperationException("CAPITAL_BASE_URL environment variable or CapitalApi:BaseUrl config is required. " +
            "Set it to the Capital.com API base URL (e.g. https://api-capital.backend-capital.com for live).");
    var apiKey = builder.Configuration["CAPITAL_API_KEY"] ?? capitalSection["ApiKey"] ?? "";
    var identifier = builder.Configuration["CAPITAL_IDENTIFIER"] ?? capitalSection["Identifier"] ?? "";
    var password = builder.Configuration["CAPITAL_PASSWORD"] ?? capitalSection["Password"] ?? "";
    var tradingEnabled = builder.Configuration.GetValue("CapitalTrading:Enabled", false);
    var preferredDemoAccountName = builder.Configuration["CAPITAL_TRADING_PREFERRED_DEMO_ACCOUNT_NAME"]
        ?? builder.Configuration["CapitalTrading:PreferredDemoAccountName"]
        ?? "DEMOAI";

    if (tradingEnabled
        && !baseUrl.Contains("demo-api-capital.backend-capital.com", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            "CapitalTrading:Enabled requires the Capital.com DEMO base URL. " +
            "Set CAPITAL_BASE_URL or CapitalApi:BaseUrl to https://demo-api-capital.backend-capital.com.");
    }

    if (!uiPriceOnly || historicalSyncEnabled)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("CAPITAL_API_KEY environment variable is required");
        if (string.IsNullOrWhiteSpace(identifier))
            throw new InvalidOperationException("CAPITAL_IDENTIFIER environment variable is required");
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("CAPITAL_PASSWORD environment variable is required");
    }
    else
    {
        Log.Information("UiPriceOnly mode enabled: live processing uses inspected Playwright prices; REST fallback disabled");
    }

    if (uiPriceOnly && historicalSyncEnabled)
    {
        Log.Information("UiPriceOnly mode enabled: live processing uses inspected Playwright prices; REST auth remains enabled for startup historical sync");
    }

    // ─── Telegram Notifications ────────────────────────────
    var telegramToken = builder.Configuration["TELEGRAM_BOT_TOKEN"]
        ?? builder.Configuration["Telegram:BotToken"]
        ?? "";
    var telegramChatIds = (builder.Configuration["Telegram:ChatIds"] ?? "1495017760")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(id => long.TryParse(id, out var v) ? v : 0L)
        .Where(id => id > 0)
        .ToList();

    builder.Services.AddSingleton<ITelegramNotifier>(sp =>
        new TelegramNotifier(
            telegramToken,
            telegramChatIds,
            sp.GetRequiredService<ILogger<TelegramNotifier>>()));

    var connString = builder.Configuration["PG_CONNECTION"]
        ?? builder.Configuration.GetConnectionString("PostgreSQL")
        ?? throw new InvalidOperationException(
            "Database connection string is not configured. " +
            "Set the PG_CONNECTION environment variable or ConnectionStrings:PostgreSQL in appsettings.json. " +
            "Example: 'Host=localhost;Port=5432;Database=ETH_BASE;Username=postgres;Password=...'");

    // DI
    builder.Services.AddSingleton<CapitalClient>(sp =>
        new CapitalClient(baseUrl, apiKey, identifier, password, preferredDemoAccountName,
            sp.GetRequiredService<ILogger<CapitalClient>>()));
    builder.Services.AddSingleton<ICapitalClient>(sp => sp.GetRequiredService<CapitalClient>());
    builder.Services.AddSingleton<ICapitalTradingClient>(sp => sp.GetRequiredService<CapitalClient>());

    // ── Tick provider chain (Playwright + REST fallback) ──────────────
    var usePlaywright = builder.Configuration.GetValue<bool>("HighFreqTicks:Enabled", defaultValue: true);
    var playwrightHeadless = builder.Configuration.GetValue<bool>("HighFreqTicks:Headless", defaultValue: true);
    var playwrightBrowserChannel = builder.Configuration["HighFreqTicks:BrowserChannel"] ?? "chrome";
    var playwrightUserDataDir = ResolveConfigPath(builder.Configuration["HighFreqTicks:UserDataDir"], workspaceRoot);
    var useRestFallback = builder.Configuration.GetValue<bool>("HighFreqTicks:UseRestFallback", defaultValue: false);
    var manualLoginTimeoutSec = builder.Configuration.GetValue<int>("HighFreqTicks:ManualLoginTimeoutSec", defaultValue: 180);
    var spikeFilterPct = builder.Configuration.GetValue<decimal>("HighFreqTicks:SpikeFilterPct", defaultValue: 0.5m);

    Log.Information(
        "Tick provider config: PlaywrightEnabled={PlaywrightEnabled} UiPriceOnly={UiPriceOnly} Headless={Headless} Channel={Channel} RestFallback={RestFallback}",
        usePlaywright, uiPriceOnly, playwrightHeadless, playwrightBrowserChannel, useRestFallback);

    if (usePlaywright)
    {
        builder.Services.AddSingleton<PlaywrightTickProvider>(sp =>
            new PlaywrightTickProvider(
                sp.GetRequiredService<ILogger<PlaywrightTickProvider>>(),
                headless: playwrightHeadless,
                browserChannel: playwrightBrowserChannel,
                userDataDir: playwrightUserDataDir,
                manualLoginTimeoutSec: manualLoginTimeoutSec));

        if (useRestFallback && !uiPriceOnly)
        {
            builder.Services.AddSingleton<HybridTickProvider>(sp =>
                new HybridTickProvider(
                    sp.GetRequiredService<PlaywrightTickProvider>(),
                    sp.GetRequiredService<ICapitalClient>(),
                    sp.GetRequiredService<ILogger<HybridTickProvider>>()));

            builder.Services.AddSingleton<ITickProvider>(sp =>
                new TickSpikeFilter(
                    sp.GetRequiredService<HybridTickProvider>(),
                    maxDeviationPct: spikeFilterPct,
                    sp.GetRequiredService<ILogger<TickSpikeFilter>>()));
        }
        else
        {
            builder.Services.AddSingleton<ITickProvider>(sp =>
                new TickSpikeFilter(
                    sp.GetRequiredService<PlaywrightTickProvider>(),
                    maxDeviationPct: spikeFilterPct,
                    sp.GetRequiredService<ILogger<TickSpikeFilter>>()));
        }
    }
    else
    {
        // Fallback: REST-only tick provider (existing behavior)
        builder.Services.AddSingleton<ITickProvider>(sp =>
            new RestTickProvider(
                sp.GetRequiredService<ICapitalClient>(),
                sp.GetRequiredService<ILogger<RestTickProvider>>()));
    }

    builder.Services.AddSingleton<IDbMigrator>(_ => new DbMigrator(connString));
    builder.Services.AddSingleton<ICandleRepository>(_ => new CandleRepository(connString));
    builder.Services.AddSingleton<ICandleSyncRepository>(_ => new CandleSyncRepository(connString));
    builder.Services.AddSingleton<IAuditRepository>(_ => new AuditRepository(connString));
    builder.Services.AddSingleton<IIndicatorRepository>(_ => new IndicatorRepository(connString));
    builder.Services.AddSingleton<IRegimeRepository>(_ => new RegimeRepository(connString));
    builder.Services.AddSingleton<ISignalRepository>(_ => new SignalRepository(connString));
    builder.Services.AddSingleton<ITickSnapshotRepository>(_ => new TickSnapshotRepository(connString));
    builder.Services.AddSingleton<IDecisionAuditRepository>(sp =>
        new DecisionAuditRepository(connString,
            sp.GetRequiredService<ILogger<DecisionAuditRepository>>()));
    builder.Services.AddSingleton<IBlockedSignalHistoryService>(sp =>
        new BlockedSignalHistoryService(
            connString,
            sp.GetRequiredService<ICandleRepository>(),
            sp.GetRequiredService<ILogger<BlockedSignalHistoryService>>()));
    builder.Services.AddSingleton<IBlockedSignalOutcomeRepository>(_ => new BlockedSignalOutcomeRepository(connString));
    builder.Services.AddSingleton<IGeneratedSignalHistoryService>(sp =>
        new GeneratedSignalHistoryService(
            connString,
            sp.GetRequiredService<ICandleRepository>(),
            sp.GetRequiredService<ILogger<GeneratedSignalHistoryService>>()));
    builder.Services.AddSingleton<IGeneratedSignalOutcomeRepository>(_ => new GeneratedSignalOutcomeRepository(connString));
    builder.Services.AddSingleton<IParameterRepository>(_ => new ParameterRepository(connString));
    builder.Services.AddSingleton<IPortalOverridesRepository>(_ => new PostgresPortalOverridesRepository(connString));
    builder.Services.AddSingleton<IExecutedTradeRepository>(_ => new ExecutedTradeRepository(connString));
    builder.Services.AddSingleton<ITradeExecutionQueueRepository>(_ => new TradeExecutionQueueRepository(connString));
    builder.Services.AddSingleton<IReplayRepository>(_ => new ReplayRepository(connString));
    builder.Services.AddSingleton<IOptimizerRepository>(_ => new OptimizerRepository(connString));
    builder.Services.AddSingleton<IParameterProvider>(sp =>
        new ParameterProvider(
            sp.GetRequiredService<IParameterRepository>(),
            overridesRepo: sp.GetRequiredService<IPortalOverridesRepository>()));
    builder.Services.AddSingleton<HistoricalReplayService>();
    builder.Services.AddSingleton<OptimizerService>();
    builder.Services.AddSingleton<MarketStateCache>();
    builder.Services.AddSingleton<BackfillService>();
    builder.Services.AddSingleton<CandleSyncState>();
    builder.Services.AddSingleton<HistoricalCandleSyncService>();
    builder.Services.AddSingleton<LiveTickProcessor>();
    // ML Enhancement services
    builder.Services.AddSingleton<IMlModelRepository>(_ => new MlModelRepository(connString));
    builder.Services.AddSingleton<IMlPredictionRepository>(_ => new MlPredictionRepository(connString));
    builder.Services.AddSingleton<IMlFeatureRepository>(_ => new MlFeatureRepository(connString));
    builder.Services.AddSingleton<IMlTrainingRunRepository>(_ => new MlTrainingRunRepository(connString));
    builder.Services.AddSingleton<IMlDataDiagnosticsRepository>(_ => new MlDataDiagnosticsRepository(connString));
    builder.Services.AddSingleton<IBtcContextProvider, NullBtcContextProvider>();
    builder.Services.AddSingleton<IDerivativesContextProvider, NullDerivativesContextProvider>();
    builder.Services.AddSingleton<MlInferenceService>();
    builder.Services.AddSingleton<MlModelPromotionService>();
    builder.Services.AddSingleton<IBlockedSignalOutcomeSyncService, BlockedSignalOutcomeSyncService>();
    builder.Services.AddSingleton<IGeneratedSignalOutcomeSyncService, GeneratedSignalOutcomeSyncService>();
    builder.Services.AddSingleton<IBlockedMlFeatureBackfillService, BlockedMlFeatureBackfillService>();
    builder.Services.AddSingleton<IMlDataDiagnosticsService, MlDataDiagnosticsService>();
    builder.Services.AddSingleton<MlDriftDetector>();
    builder.Services.AddSingleton<IAdaptiveStateRepository>(sp =>
        new AdaptiveStateRepository(connString,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<AdaptiveStateRepository>()));
    builder.Services.AddSingleton<MarketAdaptiveParameterService>();
    builder.Services.AddSingleton(sp =>
        new AdaptiveParameterLogRepository(connString,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<AdaptiveParameterLogRepository>()));
    builder.Services.AddSingleton<SignalFrequencyManager>();
    builder.Services.AddSingleton<IExecutionCandidateMapper, ExecutionCandidateMapper>();
    builder.Services.AddSingleton<TradeExecutionRuntimeState>();
    builder.Services.AddSingleton<IAccountSnapshotService, AccountSnapshotService>();
    builder.Services.AddSingleton<ITradeExecutionPolicy, TradeExecutionPolicy>();
    builder.Services.AddSingleton<ITradeExecutionService, TradeExecutionService>();
    builder.Services.AddSingleton<ITradeExecutionQueueService, TradeExecutionQueueService>();
    builder.Services.AddSingleton<IExecutedTradeResetService>(sp =>
        new ExecutedTradeResetService(
            connString,
            sp.GetRequiredService<TradeExecutionRuntimeState>(),
            sp.GetRequiredService<ILogger<ExecutedTradeResetService>>()));
    builder.Services.AddSingleton<TradeLifecycleReconciliationService>();
    builder.Services.AddSingleton<MlTrainingState>();
    builder.Services.AddSingleton<MlTrainingService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<MlTrainingService>());
    builder.Services.AddHostedService<TradeAutoExecutionService>();
    builder.Services.AddHostedService<TradeExecutionQueueWorkerService>();
    builder.Services.AddHostedService<ExecutedTradeMonitorService>();
    builder.Services.AddHostedService<DataIngestionService>();
    // B-15: StopHost on unhandled exception so app doesn't run in degraded state
    builder.Services.Configure<HostOptions>(opts =>
        opts.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost);

    var app = builder.Build();

    // Ensure portal_overrides table exists before first parameter refresh
    await app.Services.GetRequiredService<IPortalOverridesRepository>().EnsureTableExistsAsync();

    app.UseDefaultFiles();
    app.UseStaticFiles();

    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate, max-age=0";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers.Expires = "0";
        }

        await next();
    });

    var symbol = builder.Configuration["CapitalApi:Symbol"] ?? "ETHUSD";

    // ─── Health (P8-05) ─────────────────────────────────────
    app.MapGet("/health", async (MarketStateCache cache, IAuditRepository auditRepo, ICandleSyncRepository syncRepo, CandleSyncState syncState, IServiceProvider services) =>
    {
        var health = cache.GetHealthInfo();
        var liveTickProcessor = services.GetService<LiveTickProcessor>();
        var stale = health.LastTickTime.HasValue
            && (DateTimeOffset.UtcNow - health.LastTickTime.Value).TotalSeconds > 30;

        // U-06: Include gap diagnostics in health endpoint
        GapDiagnostics? gapDiag = null;
        try { gapDiag = await auditRepo.GetGapDiagnosticsAsync(symbol); }
        catch { /* don't let diagnostics failure break health check */ }

        // Startup historical candle sync summary (in-memory cache populated by DataIngestionService)
        var sync = syncState.Latest;
        object? historySync = null;
        if (sync != null)
        {
            historySync = new
            {
                status = sync.Status,
                readyTimeframes = sync.ReadyTimeframes,
                totalTimeframes = sync.TotalTimeframes,
                failedTimeframes = sync.FailedTimeframes,
                runningTimeframes = sync.Timeframes
                    .Where(t => t.Status == TimeframeSyncStatus.Running)
                    .Select(t => t.Tf.Name)
                    .ToArray(),
                noopTimeframes = sync.NoopTimeframes,
                startedAtUtc = sync.StartedAtUtc,
                finishedAtUtc = sync.FinishedAtUtc,
                lastSuccessAt = sync.FinishedAtUtc
            };
        }
        else
        {
            var rows = await syncRepo.GetAllAsync(symbol);
            if (rows.Count > 0)
            {
                var ready = rows.Count(r => r.Status == TimeframeSyncStatus.Ready);
                var failed = rows.Count(r => r.Status == TimeframeSyncStatus.Failed);
                var running = rows.Count(r => r.Status == TimeframeSyncStatus.Running);
                var startedAtUtc = rows
                    .Where(r => r.LastRunStartedAtUtc.HasValue)
                    .Select(r => r.LastRunStartedAtUtc!.Value)
                    .DefaultIfEmpty()
                    .Min();
                var finishedAtUtc = rows.All(r => r.LastRunFinishedAtUtc.HasValue)
                    ? rows.Select(r => r.LastRunFinishedAtUtc!.Value).Max()
                    : (DateTimeOffset?)null;
                var lastSuccessAt = rows
                    .Where(r => r.LastSuccessAtUtc.HasValue)
                    .Select(r => r.LastSuccessAtUtc!.Value)
                    .DefaultIfEmpty()
                    .Max();

                historySync = new
                {
                    status = failed > 0
                        ? TimeframeSyncStatus.Failed
                        : ready == rows.Count
                            ? TimeframeSyncStatus.Ready
                            : TimeframeSyncStatus.Running,
                    readyTimeframes = ready,
                    totalTimeframes = rows.Count,
                    failedTimeframes = failed,
                    runningTimeframes = rows
                        .Where(r => r.Status == TimeframeSyncStatus.Running)
                        .Select(r => r.Timeframe)
                        .ToArray(),
                    noopTimeframes = rows.Count(r => r.SyncMode == TimeframeSyncMode.Noop),
                    startedAtUtc = startedAtUtc == default ? (DateTimeOffset?)null : startedAtUtc,
                    finishedAtUtc,
                    lastSuccessAt = lastSuccessAt == default ? (DateTimeOffset?)null : lastSuccessAt
                };
            }
        }

        return Results.Ok(new
        {
            status = stale ? "stale" : "running",
            timestamp = DateTimeOffset.UtcNow,
            lastTickTime = health.LastTickTime,
            tickCount = health.TickCount,
            lastCandleClose = health.LastCandleCloseTime,
            lastCandleTimeframe = health.LastCandleTimeframe,
            lastError = health.LastError,
            isStale = stale,
            missedSignalsDueToExceptions = liveTickProcessor?.MissedSignalDueToExceptionCount ?? 0,
            lastEvaluationExceptionUtc = liveTickProcessor?.LastEvaluationExceptionUtc,
            gaps = gapDiag != null ? new
            {
                unresolvedRecent = gapDiag.UnresolvedRecentCount,
                unresolvedTotal = gapDiag.UnresolvedTotalCount,
                oldestUnresolved = gapDiag.OldestUnresolvedExpectedTime,
                newestDetected = gapDiag.NewestDetectedAtUtc
            } : null,
            historySync
        });
    });

    // ─── Phase 8 API Endpoints ────────────────────────────

    // P8-01 FIX: Read from internal cache, not broker directly
    app.MapGet("/api/quote/current", (MarketStateCache cache) =>
    {
        var spot = cache.LatestSpot;
        if (spot == null)
            return Results.Ok(new { Bid = 0m, Ask = 0m, Mid = 0m, Timestamp = DateTimeOffset.UtcNow });
        return Results.Ok(new { spot.Bid, spot.Ask, spot.Mid, spot.Timestamp });
    });

    app.MapGet("/api/candles", async (ICandleRepository repo, string? timeframe, int? limit) =>
    {
        var tf = (timeframe ?? "5m") switch
        {
            "1m" => Timeframe.M1,
            "5m" => Timeframe.M5,
            "15m" => Timeframe.M15,
            "30m" => Timeframe.M30,
            "1h" => Timeframe.H1,
            "4h" => Timeframe.H4,
            _ => (Timeframe?)null
        };
        if (tf == null)
            return Results.BadRequest(new { error = $"Unrecognized timeframe: {timeframe}" });
        var closed = await repo.GetClosedCandlesAsync(tf, symbol, limit ?? 200);
        var open = await repo.GetOpenCandleAsync(tf, symbol);

        var allCandles = closed
            .Where(c => c.MidClose > 0)
            .ToList();

        if (open != null && open.MidClose > 0)
        {
            // Avoid duplicate timestamps when the latest closed candle and running candle share OpenTime.
            allCandles.RemoveAll(c => c.OpenTime == open.OpenTime);
            allCandles.Add(open);
        }

        allCandles = allCandles
            .GroupBy(c => c.OpenTime)
            .Select(g => g.OrderByDescending(x => x.IsClosed).First())
            .OrderBy(c => c.OpenTime)
            .ToList();

        return Results.Ok(allCandles.Select(c => new
        {
            c.OpenTime,
            c.BidOpen,
            c.BidHigh,
            c.BidLow,
            c.BidClose,
            c.AskOpen,
            c.AskHigh,
            c.AskLow,
            c.AskClose,
            c.MidOpen,
            c.MidHigh,
            c.MidLow,
            c.MidClose,
            c.Volume,
            c.BuyerPct,
            c.SellerPct
        }));
    });

    app.MapGet("/api/indicators/current", async (MarketStateCache cache, ICandleRepository candleRepo, IIndicatorRepository repo, IParameterProvider paramProvider) =>
    {
        // Cache-first read path (updated by LiveTickProcessor every ~1s)
        IndicatorSnapshot? snap1m = cache.GetCachedIndicator("1m");
        IndicatorSnapshot? snap5m = cache.GetCachedIndicator("5m");
        IndicatorSnapshot? snap15m = cache.GetCachedIndicator("15m");
        IndicatorSnapshot? snap30m = cache.GetCachedIndicator("30m");
        IndicatorSnapshot? snap1h = cache.GetCachedIndicator("1h");
        IndicatorSnapshot? snap4h = cache.GetCachedIndicator("4h");

        // Fill cache misses from live recomputation (closed + running candle)
        var warmUp = paramProvider.GetActive().WarmUpPeriod;

        foreach (var tf in new[] { Timeframe.M1, Timeframe.M5, Timeframe.M15, Timeframe.M30, Timeframe.H1, Timeframe.H4 })
        {
            try
            {
                var tfName = tf.Name;
                var needsRefresh = tfName switch
                {
                    "1m" => snap1m == null,
                    "5m" => snap5m == null,
                    "15m" => snap15m == null,
                    "30m" => snap30m == null,
                    "1h" => snap1h == null,
                    "4h" => snap4h == null,
                    _ => false
                };

                if (!needsRefresh)
                    continue;

                var closed = await candleRepo.GetClosedCandlesAsync(tf, symbol, warmUp + 10);
                var open = await candleRepo.GetOpenCandleAsync(tf, symbol);

                var allCandles = closed.ToList();
                if (open != null) allCandles.Add(open);

                if (allCandles.Count >= warmUp)
                {
                    var snapshot = IndicatorEngine.ComputeLatest(symbol, tf.Name, allCandles);
                    if (snapshot != null)
                    {
                        cache.UpdateIndicator(tf.Name, snapshot);
                        if (tf == Timeframe.M1) snap1m = snapshot;
                        else if (tf == Timeframe.M5) snap5m = snapshot;
                        else if (tf == Timeframe.M15) snap15m = snapshot;
                        else if (tf == Timeframe.M30) snap30m = snapshot;
                        else if (tf == Timeframe.H1) snap1h = snapshot;
                        else if (tf == Timeframe.H4) snap4h = snapshot;
                    }
                }
            }
            catch { /* fall back to DB */ }
        }

        // Fall back to stored snapshots if live computation failed
        snap1m ??= await repo.GetLatestAsync(symbol, "1m");
        snap5m ??= await repo.GetLatestAsync(symbol, "5m");
        snap15m ??= await repo.GetLatestAsync(symbol, "15m");
        snap30m ??= await repo.GetLatestAsync(symbol, "30m");
        snap1h ??= await repo.GetLatestAsync(symbol, "1h");
        snap4h ??= await repo.GetLatestAsync(symbol, "4h");

        return Results.Ok(new { oneMin = snap1m, fiveMin = snap5m, fifteenMin = snap15m, thirtyMin = snap30m, oneHour = snap1h, fourHour = snap4h });
    });

    app.MapGet("/api/indicators/history", async (IIndicatorRepository repo, string? timeframe, int? limit) =>
    {
        var tf = timeframe ?? "5m";
        var from = DateTimeOffset.UtcNow.AddDays(-7);
        var to = DateTimeOffset.UtcNow;
        var snapshots = await repo.GetSnapshotsAsync(symbol, tf, from, to);
        var result = snapshots.TakeLast(limit ?? 200).Select(s => new
        {
            s.CandleOpenTimeUtc,
            s.Ema20,
            s.Ema50,
            s.Rsi14,
            s.Macd,
            s.MacdSignal,
            s.MacdHist,
            s.Atr14,
            s.Adx14,
            s.PlusDi,
            s.MinusDi,
            s.VolumeSma20,
            s.Vwap,
            s.Spread,
            s.CloseMid
        });
        return Results.Ok(result);
    });

    app.MapGet("/api/regime/current", async (MarketStateCache cache, IRegimeRepository repo) =>
    {
        var primary = cache.GetCachedRegime("15m") ?? await repo.GetLatestAsync(symbol) ?? new RegimeResult
        {
            Symbol = symbol,
            CandleOpenTimeUtc = DateTimeOffset.UtcNow,
            Regime = Regime.NEUTRAL,
            RegimeScore = 0,
            TriggeredConditions = [],
            DisqualifyingConditions = ["No data"]
        };

        var perTf = cache.GetRegimeCacheSnapshot()
            .Where(kv => kv.Key is "1m" or "5m" or "15m" or "30m" or "1h" or "4h")
            .OrderBy(kv => kv.Key)
            .ToDictionary(
                kv => kv.Key,
                kv => new
                {
                    regime = kv.Value.Regime.ToString(),
                    regimeScore = kv.Value.RegimeScore,
                    candleOpenTimeUtc = kv.Value.CandleOpenTimeUtc,
                    triggeredConditions = kv.Value.TriggeredConditions,
                    disqualifyingConditions = kv.Value.DisqualifyingConditions
                });

        return Results.Ok(new
        {
            regime = primary.Regime.ToString(),
            regimeScore = primary.RegimeScore,
            candleOpenTimeUtc = primary.CandleOpenTimeUtc,
            triggeredConditions = primary.TriggeredConditions,
            disqualifyingConditions = primary.DisqualifyingConditions,
            primary = new
            {
                timeframe = "15m",
                regime = primary.Regime.ToString(),
                regimeScore = primary.RegimeScore,
                candleOpenTimeUtc = primary.CandleOpenTimeUtc,
                triggeredConditions = primary.TriggeredConditions,
                disqualifyingConditions = primary.DisqualifyingConditions
            },
            perTimeframe = perTf
        });
    });

    // T2-6: Accept optional ?timeframe= param so dashboard can query any TF including 1m scalps.
    // Default is the active primary timeframe. Also returns latest 1m scalp for dashboard awareness.
    app.MapGet("/api/signals/latest", async (ISignalRepository repo, IParameterProvider paramProvider, string? timeframe) =>
    {
        var p = paramProvider.GetActive();
        var resolvedTf = string.IsNullOrWhiteSpace(timeframe) ? p.TimeframePrimary : timeframe.Trim().ToLowerInvariant();
        var sig = await repo.GetLatestPrimaryTimeframeSignalAsync(symbol, resolvedTf);

        // Also surface the most recent 1m scalp signal for composite awareness
        SignalRecommendation? scalp1m = null;
        if (resolvedTf != "1m")
            scalp1m = await repo.GetLatestPrimaryTimeframeSignalAsync(symbol, "1m");

        return Results.Ok(new
        {
            signal = sig,
            scalpSignal1m = scalp1m,
            resolvedTimeframe = resolvedTf,
            direction = sig?.Direction.ToString() ?? "NO_DATA"
        });
    });

    // Dashboard composite endpoint: latest signal + decision + ML prediction in one call
    app.MapGet("/api/dashboard/latest", async (
        ISignalRepository signalRepo,
        IDecisionAuditRepository decisionRepo,
        IMlPredictionRepository mlPredictionRepo,
        IParameterProvider paramProvider) =>
    {
        var primaryTimeframe = paramProvider.GetActive().TimeframePrimary;
        var latestSignal = await signalRepo.GetLatestSignalAsync(symbol);
        SignalDecision? linkedDecision = null;
        if (latestSignal?.EvaluationId is Guid evaluationId && evaluationId != Guid.Empty)
            linkedDecision = await decisionRepo.GetDecisionByEvaluationIdAsync(evaluationId);

        var latestDecision = await decisionRepo.GetLatestDecisionAsync(symbol);
        var decisionForCard = latestSignal == null ? latestDecision : linkedDecision;
        MlPrediction? latestPrediction = null;
        if (latestSignal != null)
            latestPrediction = await mlPredictionRepo.GetBySignalIdAsync(latestSignal.SignalId);

        var predictionTimeframe = latestSignal?.Timeframe ?? primaryTimeframe;
        latestPrediction ??= await mlPredictionRepo.GetLatestAsync(symbol, predictionTimeframe, "actionable");
        latestPrediction ??= await mlPredictionRepo.GetLatestAsync(symbol, predictionTimeframe, "all");

        static object? MapDecision(SignalDecision? decision)
            => decision != null ? new
            {
                decisionId = decision.DecisionId,
                evaluationId = decision.EvaluationId == Guid.Empty ? (Guid?)null : decision.EvaluationId,
                symbol = decision.Symbol,
                timeframe = decision.Timeframe,
                barTime = decision.BarTimeUtc,
                decisionTime = decision.DecisionTimeUtc,
                decisionType = decision.DecisionType.ToString(),
                outcomeCategory = decision.OutcomeCategory.ToString(),
                regime = decision.UsedRegime?.ToString(),
                reasonCodes = decision.ReasonCodes.Select(r => r.ToString()).ToList(),
                reasonDetails = decision.ReasonDetails,
                confidenceScore = decision.ConfidenceScore,
                blendedConfidence = decision.BlendedConfidence,
                effectiveThreshold = decision.EffectiveThreshold,
                sourceMode = decision.SourceMode.ToString()
            } : null;

        return Results.Ok(new
        {
            signal = latestSignal,
            decision = MapDecision(decisionForCard),
            latestDecision = latestDecision?.DecisionId == decisionForCard?.DecisionId
                ? null
                : MapDecision(latestDecision),
            mlPrediction = latestPrediction
        });
    });

    app.MapGet("/api/signals/history", async (ISignalRepository repo, IExecutedTradeRepository executedTradeRepository, int? limit, int? page, CancellationToken ct) =>
    {
        var pageNum = Math.Max(1, page ?? 1);
        var pageSize = Math.Clamp(limit ?? 50, 1, 500);
        var signals = await repo.GetSignalHistoryWithOutcomesAsync(symbol, pageSize, (pageNum - 1) * pageSize, ct);
        var executionBySignal = await executedTradeRepository.GetLatestBySourceSignalsAsync(
            signals.Select(s => s.Signal.SignalId).ToArray(),
            SignalExecutionSourceType.Recommended,
            ct);
        var total = await repo.GetSignalCountAsync(symbol);
        return Results.Ok(new
        {
            signals = signals.Select(item => new
            {
                signal = item.Signal,
                outcome = item.Outcome,
                execution = executionBySignal.TryGetValue(item.Signal.SignalId, out var execution) ? execution : null
            }),
            total,
            page = pageNum,
            pageSize
        });
    });

    app.MapGet("/api/blocked-signals/history", async (
        IBlockedSignalHistoryService blockedSignalHistory,
        IExecutedTradeRepository executedTradeRepository,
        int? limit,
        int? page,
        CancellationToken ct) =>
    {
        var pageNum = Math.Max(1, page ?? 1);
        var pageSize = Math.Clamp(limit ?? 50, 1, 2000);
        var result = await blockedSignalHistory.GetHistoryAsync(symbol, pageSize, (pageNum - 1) * pageSize, ct);
        var executionBySignal = await executedTradeRepository.GetLatestBySourceSignalsAsync(
            result.Signals.Select(s => s.Signal.SignalId).ToArray(),
            SignalExecutionSourceType.Blocked,
            ct);
        return Results.Ok(new
        {
            signals = result.Signals.Select(item => new
            {
                signal = item.Signal,
                outcome = item.Outcome,
                execution = executionBySignal.TryGetValue(item.Signal.SignalId, out var execution) ? execution : null
            }),
            stats = result.Stats,
            total = result.Total,
            page = result.Page,
            pageSize = result.PageSize
        });
    });

    app.MapGet("/api/generated-signals/history", async (
        IGeneratedSignalHistoryService generatedSignalHistory,
        IExecutedTradeRepository executedTradeRepository,
        int? limit,
        int? page,
        int? hours,
        CancellationToken ct) =>
    {
        var pageNum = Math.Max(1, page ?? 1);
        var pageSize = Math.Clamp(limit ?? 50, 1, 2000);
        var result = await generatedSignalHistory.GetHistoryAsync(symbol, pageSize, (pageNum - 1) * pageSize, hours, ct);
        var executionBySignal = await executedTradeRepository.GetLatestBySourceSignalsAsync(
            result.Signals.Select(s => s.Signal.SignalId).ToArray(),
            SignalExecutionSourceType.Generated,
            ct);
        return Results.Ok(new
        {
            signals = result.Signals.Select(item => new
            {
                signal = item.Signal,
                outcome = item.Outcome,
                execution = executionBySignal.TryGetValue(item.Signal.SignalId, out var execution) ? execution : null
            }),
            stats = result.Stats,
            total = result.Total,
            page = result.Page,
            pageSize = result.PageSize
        });
    });

    app.MapGet("/api/executed-trades", async (
        IExecutedTradeRepository repository,
        ICapitalTradingClient capitalTradingClient,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? instrument,
        string? direction,
        string? timeframe,
        string? sourceType,
        string? status,
        int? limit,
        int? page,
        CancellationToken ct) =>
    {
        var pageSize = Math.Clamp(limit ?? 50, 1, 200);
        var pageNum = Math.Max(1, page ?? 1);
        var query = new ExecutedTradeQuery
        {
            FromUtc = from,
            ToUtc = to,
            Instrument = instrument,
            Direction = Enum.TryParse<SignalDirection>(direction, true, out var parsedDirection) ? parsedDirection : null,
            Timeframe = timeframe,
            SourceType = Enum.TryParse<SignalExecutionSourceType>(sourceType, true, out var parsedSource) ? parsedSource : null,
            Status = Enum.TryParse<ExecutedTradeStatus>(status, true, out var parsedStatus) ? parsedStatus : null,
            Limit = pageSize,
            Offset = (pageNum - 1) * pageSize
        };
        if (capitalTradingClient.IsDemoEnvironment)
        {
            query = query with
            {
                AccountName = preferredDemoAccountName,
                IsDemo = true
            };
        }
        var trades = await repository.GetExecutedTradesAsync(query, ct);
        var total = await repository.GetExecutedTradeCountAsync(query with { Limit = 0, Offset = 0 }, ct);
        return Results.Ok(new { trades, total, page = pageNum, pageSize });
    });

    app.MapGet("/api/executed-trades/{id:long}", async (
        long id,
        IExecutedTradeRepository repository,
        CancellationToken ct) =>
    {
        var trade = await repository.GetExecutedTradeAsync(id, ct);
        return trade == null ? Results.NotFound() : Results.Ok(trade);
    });

    app.MapPost("/api/executed-trades/execute-signal/{signalId:guid}", async (
        Guid signalId,
        HttpContext ctx,
        ISignalRepository signalRepository,
        IExecutionCandidateMapper mapper,
        ITradeExecutionQueueService queueService,
        CancellationToken ct) =>
    {
        if (RejectIfNotLoopback(ctx) is { } reject) return reject;
        var signal = await signalRepository.GetSignalByIdAsync(signalId, ct);
        if (signal == null) return Results.NotFound(new { error = "Signal not found." });
        var result = await queueService.EnqueueAsync(new TradeExecutionRequest
        {
            Candidate = mapper.FromRecommended(signal),
            RequestedBy = "dashboard"
        }, ct);
        return result.Accepted ? Results.Ok(result) : Results.BadRequest(result);
    });

    app.MapPost("/api/executed-trades/execute-generated/{signalId:guid}", async (
        Guid signalId,
        HttpContext ctx,
        IGeneratedSignalHistoryService generatedHistory,
        IExecutionCandidateMapper mapper,
        ITradeExecutionQueueService queueService,
        CancellationToken ct) =>
    {
        if (RejectIfNotLoopback(ctx) is { } reject) return reject;
        var signal = await generatedHistory.GetBySignalIdAsync(symbol, signalId, ct);
        if (signal == null) return Results.NotFound(new { error = "Generated signal not found." });
        var result = await queueService.EnqueueAsync(new TradeExecutionRequest
        {
            Candidate = mapper.FromGenerated(signal.Signal),
            RequestedBy = "dashboard"
        }, ct);
        return result.Accepted ? Results.Ok(result) : Results.BadRequest(result);
    });

    app.MapPost("/api/executed-trades/execute-blocked/{signalId:guid}", async (
        Guid signalId,
        HttpContext ctx,
        IBlockedSignalHistoryService blockedHistory,
        IExecutionCandidateMapper mapper,
        ITradeExecutionQueueService queueService,
        CancellationToken ct) =>
    {
        if (RejectIfNotLoopback(ctx) is { } reject) return reject;
        var signal = await blockedHistory.GetBySignalIdAsync(symbol, signalId, ct);
        if (signal == null) return Results.NotFound(new { error = "Blocked signal not found." });
        var result = await queueService.EnqueueAsync(new TradeExecutionRequest
        {
            Candidate = mapper.FromBlocked(signal.Signal),
            RequestedBy = "dashboard"
        }, ct);
        return result.Accepted ? Results.Ok(result) : Results.BadRequest(result);
    });

    app.MapPost("/api/executed-trades/{id:long}/force-close", async (
        long id,
        HttpContext ctx,
        ForceCloseRequest request,
        ITradeExecutionService executionService,
        CancellationToken ct) =>
    {
        if (RejectIfNotLoopback(ctx) is { } reject) return reject;
        var result = await executionService.ForceCloseAsync(id, request, ct);
        return result.Success ? Results.Ok(result) : Results.BadRequest(result);
    });

    app.MapPost("/api/executed-trades/reset", async (
        HttpContext ctx,
        IExecutedTradeResetService resetService,
        CancellationToken ct) =>
    {
        if (RejectIfNotLoopback(ctx) is { } reject) return reject;
        var result = await resetService.ResetAsync(ct);
        return Results.Ok(result);
    });

    app.MapGet("/api/trading/account-summary", async (
        IAccountSnapshotService accountSnapshotService,
        IExecutedTradeRepository repository,
        ICapitalTradingClient capitalTradingClient,
        CancellationToken ct) =>
    {
        try
        {
            var snapshot = await accountSnapshotService.GetLatestAsync(ct);
            return Results.Ok(snapshot);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Trading account summary refresh failed; falling back to latest persisted snapshot");
            var fallback = capitalTradingClient.IsDemoEnvironment
                ? await repository.GetLatestAccountSnapshotAsync(preferredDemoAccountName, isDemo: true, ct)
                : await repository.GetLatestAccountSnapshotAsync(ct);
            if (fallback != null)
                return Results.Ok(fallback);

            return Results.Problem(
                title: "Trading account summary unavailable",
                detail: ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    });

    app.MapGet("/api/trading/open-positions", async (
        ICapitalTradingClient capitalTradingClient,
        CancellationToken ct) =>
    {
        await capitalTradingClient.EnsureDemoReadyAsync(ct);
        var positions = await capitalTradingClient.GetOpenPositionsAsync(ct);
        return Results.Ok(new { positions });
    });

    app.MapGet("/api/trading/execution-stats", async (
        IExecutedTradeRepository repository,
        ICapitalTradingClient capitalTradingClient,
        CancellationToken ct) =>
    {
        var statsQuery = capitalTradingClient.IsDemoEnvironment
            ? new ExecutedTradeQuery
            {
                AccountName = preferredDemoAccountName,
                IsDemo = true
            }
            : new ExecutedTradeQuery();
        var stats = await repository.GetExecutionStatsAsync(statsQuery, ct);
        return Results.Ok(stats);
    });

    app.MapGet("/api/trading/health", async (
        ITradeExecutionPolicy policy,
        ICapitalTradingClient capitalTradingClient,
        IExecutedTradeRepository repository,
        TradeExecutionRuntimeState runtimeState,
        CancellationToken ct) =>
    {
        var settings = policy.GetSettings();
        var latestSnapshot = capitalTradingClient.IsDemoEnvironment
            ? await repository.GetLatestAccountSnapshotAsync(preferredDemoAccountName, isDemo: true, ct)
            : await repository.GetLatestAccountSnapshotAsync(ct);
        var effectiveAccountName = runtimeState.ActiveAccountName ?? latestSnapshot?.AccountName;
        return Results.Ok(new BrokerHealthSnapshot
        {
            DemoOnly = settings.DemoOnly && capitalTradingClient.IsDemoEnvironment,
            SessionReady = runtimeState.SessionReady,
            ExecutionEnabled = settings.Enabled,
            RequiredDemoAccountName = preferredDemoAccountName,
            AccountName = effectiveAccountName,
            AccountId = runtimeState.ActiveAccountId ?? latestSnapshot?.AccountId,
            ActiveAccountIsDemo = runtimeState.ActiveAccountIsDemo ?? latestSnapshot?.IsDemo,
            ActiveAccountMatchesRequiredDemo = capitalTradingClient.IsDemoEnvironment
                ? !string.IsNullOrWhiteSpace(effectiveAccountName)
                    && string.Equals(effectiveAccountName, preferredDemoAccountName, StringComparison.Ordinal)
                : null,
            LastSyncUtc = runtimeState.LastSyncUtc ?? latestSnapshot?.CapturedAtUtc,
            LatestAccountResolutionUtc = runtimeState.LastAccountResolutionUtc,
            AccountSelectionSource = runtimeState.AccountSelectionSource,
            LatestExecutionAccountId = runtimeState.LatestExecutionAccountId,
            LatestExecutionAccountName = runtimeState.LatestExecutionAccountName,
            LatestBrokerError = runtimeState.LatestBrokerError,
            LatestOrderNote = runtimeState.LatestOrderNote
        });
    });

    app.MapGet("/api/trading/queue", async (
        ITradeExecutionQueueService queueService,
        int? limit,
        CancellationToken ct) =>
    {
        var snapshot = await queueService.GetSnapshotAsync(Math.Clamp(limit ?? 50, 1, 200), ct);
        return Results.Ok(snapshot);
    });

    // W-02 / W-03: Expose dashboard-relevant thresholds from server parameters
    app.MapGet("/api/config/thresholds", (IParameterProvider pp) =>
    {
        var p = pp.GetActive();
        return Results.Ok(new
        {
            maxSpreadPct = p.MaxSpreadPct,
            outcomeTimeoutBars = p.OutcomeTimeoutBars,
            uiPriceOnly = uiPriceOnly
        });
    });

    app.MapGet("/api/performance/summary", async (
        ISignalRepository repo,
        IBlockedSignalHistoryService blockedSignalHistory,
        CancellationToken ct) =>
    {
        // Signal Recommendations: all-time from signal_outcomes (actual tracked outcomes)
        var allTimeFrom = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to = DateTimeOffset.UtcNow;
        var signalOutcomes = await repo.GetOutcomesAsync(symbol, allTimeFrom, to, ct);

        // Blocked signals: hypothetical outcomes computed by OutcomeEvaluator
        // These are NOT in signal_outcomes (they were never executed)
        var blockedOutcomes = new List<SignalOutcome>();
        var firstPage = await blockedSignalHistory.GetHistoryAsync(symbol, 1, 0, ct);
        if (firstPage.Total > 0)
        {
            var allBlocked = firstPage.Total > firstPage.Signals.Count
                ? await blockedSignalHistory.GetHistoryAsync(symbol, firstPage.Total, 0, ct)
                : firstPage;
            blockedOutcomes.AddRange(allBlocked.Signals.Select(s => s.Outcome));
        }

        var allOutcomes = signalOutcomes.Concat(blockedOutcomes).ToList();
        var stats = OutcomeEvaluator.ComputeStats(allOutcomes);

        return Results.Ok(new
        {
            stats.TotalSignals,
            stats.ResolvedSignals,
            stats.Wins,
            stats.Losses,
            stats.Expired,
            stats.Ambiguous,
            stats.WinRate,
            stats.AverageR,
            stats.ProfitFactor,
            stats.TotalPnlR,
            signalRecommendationCount = signalOutcomes.Count,
            blockedSignalCount = blockedOutcomes.Count
        });
    });

    app.MapGet("/api/performance/daily", async (ISignalRepository repo) =>
    {
        var from = DateTimeOffset.UtcNow.AddDays(-7);
        var to = DateTimeOffset.UtcNow;
        var outcomes = await repo.GetOutcomesAsync(symbol, from, to);
        var stats = OutcomeEvaluator.ComputeStats(outcomes);
        return Results.Ok(new { period = "7d", stats });
    });

    // ─── TF-7: Decision Audit / Visibility Endpoints ────
    app.MapGet("/api/decisions/latest", async (IDecisionAuditRepository decisionRepo) =>
    {
        var decision = await decisionRepo.GetLatestDecisionAsync(symbol);
        if (decision == null)
            return Results.Ok(new { decisionType = "NO_DATA" });
        return Results.Ok(new
        {
            decisionId = decision.DecisionId,
            symbol = decision.Symbol,
            barTime = decision.BarTimeUtc,
            decisionTime = decision.DecisionTimeUtc,
            decisionType = decision.DecisionType.ToString(),
            outcomeCategory = decision.OutcomeCategory.ToString(),
            regime = decision.UsedRegime?.ToString(),
            regimeBarTime = decision.UsedRegimeTimestamp,
            reasonCodes = decision.ReasonCodes.Select(r => r.ToString()),
            reasonDetails = decision.ReasonDetails,
            confidenceScore = decision.ConfidenceScore,
            parameterSetId = decision.ParameterSetId,
            sourceMode = decision.SourceMode.ToString(),
            // FR-1: Signal lifecycle state
            lifecycleState = decision.LifecycleState.ToString(),
            finalBlockReason = decision.FinalBlockReason,
            // FR-4: Decision origin
            origin = decision.Origin.ToString(),
            // FR-16: Evaluation correlation key
            evaluationId = decision.EvaluationId,
            timeSinceDecision = (DateTimeOffset.UtcNow - decision.DecisionTimeUtc).TotalSeconds
        });
    });

    app.MapGet("/api/decisions/history", async (IDecisionAuditRepository decisionRepo, int? hours, int? limit) =>
    {
        var from = DateTimeOffset.UtcNow.AddHours(-(hours ?? 24));
        var to = DateTimeOffset.UtcNow;
        var decisions = await decisionRepo.GetDecisionsAsync(symbol, from, to, limit ?? 100);
        return Results.Ok(decisions.Select(d => new
        {
            d.DecisionId,
            d.BarTimeUtc,
            d.DecisionTimeUtc,
            DecisionType = d.DecisionType.ToString(),
            OutcomeCategory = d.OutcomeCategory.ToString(),
            Regime = d.UsedRegime?.ToString(),
            ReasonCodes = d.ReasonCodes.Select(r => r.ToString()),
            d.ConfidenceScore,
            SourceMode = d.SourceMode.ToString(),
            // FR-1/FR-4/FR-16
            LifecycleState = d.LifecycleState.ToString(),
            d.FinalBlockReason,
            Origin = d.Origin.ToString(),
            d.EvaluationId
        }));
    });

    app.MapGet("/api/decisions/summary", async (IDecisionAuditRepository decisionRepo, int? hours) =>
    {
        var from = hours.HasValue
            ? DateTimeOffset.UtcNow.AddHours(-hours.Value)
            : new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero); // all-time
        var to = DateTimeOffset.UtcNow;
        var summary = await decisionRepo.GetSummaryAsync(symbol, from, to);

        DateTimeOffset? lastSignalTime = summary.LastSignalTime;
        double? timeSinceLastSignalSec = lastSignalTime.HasValue
            ? (DateTimeOffset.UtcNow - lastSignalTime.Value).TotalSeconds
            : null;

        return Results.Ok(new
        {
            summary.TotalDecisions,
            summary.LongCount,
            summary.ShortCount,
            summary.NoTradeCount,
            summary.StrategyNoTradeCount,
            summary.OperationalBlockedCount,
            summary.ContextNotReadyCount,
            summary.LastSignalTime,
            summary.LastEvaluationTime,
            timeSinceLastSignalSec,
            topRejectReasons = summary.TopRejectReasons.Select(r => new { reason = r.Reason, count = r.Count })
        });
    });

    // ─── FR-18: Signal Rejection Funnel Observability ─────
    app.MapGet("/api/decisions/rejection-funnel", async (IDecisionAuditRepository decisionRepo, int? hours) =>
    {
        var from = hours.HasValue
            ? DateTimeOffset.UtcNow.AddHours(-hours.Value)
            : new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to = DateTimeOffset.UtcNow;
        var decisions = await decisionRepo.GetDecisionsAsync(symbol, from, to, 10000);
        var funnel = decisions
            .GroupBy(d => d.LifecycleState.ToString())
            .Select(g => new { stage = g.Key, count = g.Count() })
            .OrderByDescending(x => x.count)
            .ToList();
        var byOrigin = decisions
            .GroupBy(d => d.Origin.ToString())
            .Select(g => new { origin = g.Key, count = g.Count() })
            .ToList();
        var byBlockReason = decisions
            .Where(d => d.FinalBlockReason != null)
            .GroupBy(d => d.FinalBlockReason!)
            .Select(g => new { reason = g.Key, count = g.Count() })
            .OrderByDescending(x => x.count)
            .Take(20)
            .ToList();
        return Results.Ok(new
        {
            periodHours = hours ?? 24,
            totalEvaluations = decisions.Count,
            lifecycleFunnel = funnel,
            byOrigin,
            topBlockReasons = byBlockReason
        });
    });

    // ─── FR-9: ML Inference Mode Health ───────────────────
    app.MapGet("/api/ml/inference-mode", (MlInferenceService mlService) =>
    {
        return Results.Ok(new
        {
            isHeuristicFallback = mlService.IsHeuristicFallback,
            activeModelVersion = mlService.ActiveModelVersion,
            inferenceMode = mlService.IsHeuristicFallback ? "HEURISTIC_FALLBACK" : "TRAINED"
        });
    });

    // ─── Startup Candle Sync Status ───────────────────────
    // Returns persisted per-timeframe sync state from candle_sync_status, plus a
    // top-level overall view from the in-memory CandleSyncState. Dashboard polls this.
    app.MapGet("/api/admin/candle-sync/status", async (
        HttpContext ctx, ICandleSyncRepository syncRepo, CandleSyncState syncState) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        var rows = await syncRepo.GetAllAsync(symbol);
        var sync = syncState.Latest;

        var ready = rows.Count(r => r.Status == TimeframeSyncStatus.Ready);
        var failed = rows.Count(r => r.Status == TimeframeSyncStatus.Failed);
        var running = rows.Count(r => r.Status == TimeframeSyncStatus.Running);
        var fallbackStartedAtUtc = rows
            .Where(r => r.LastRunStartedAtUtc.HasValue)
            .Select(r => r.LastRunStartedAtUtc!.Value)
            .DefaultIfEmpty()
            .Min();
        var fallbackFinishedAtUtc = rows.All(r => r.LastRunFinishedAtUtc.HasValue) && rows.Count > 0
            ? rows.Select(r => r.LastRunFinishedAtUtc!.Value).Max()
            : (DateTimeOffset?)null;

        return Results.Ok(new
        {
            symbol,
            status = sync?.Status ?? (rows.Count == 0
                ? TimeframeSyncStatus.Pending
                : (failed > 0 ? TimeframeSyncStatus.Failed
                    : ready == rows.Count ? TimeframeSyncStatus.Ready
                    : TimeframeSyncStatus.Running)),
            startedAtUtc = sync?.StartedAtUtc ?? (fallbackStartedAtUtc == default ? null : fallbackStartedAtUtc),
            finishedAtUtc = sync?.FinishedAtUtc ?? fallbackFinishedAtUtc,
            totalTimeframes = rows.Count > 0 ? rows.Count : (sync?.TotalTimeframes ?? 0),
            readyTimeframes = ready,
            failedTimeframes = failed,
            runningTimeframes = running,
            timeframes = rows.Select(r => new
            {
                timeframe = r.Timeframe,
                status = r.Status,
                syncMode = r.SyncMode,
                isTableEmpty = r.IsTableEmpty,
                requestedFromUtc = r.RequestedFromUtc,
                requestedToUtc = r.RequestedToUtc,
                lastExistingCandleUtc = r.LastExistingCandleUtc,
                lastSyncedCandleUtc = r.LastSyncedCandleUtc,
                offlineDurationSec = r.OfflineDurationSec,
                chunkSizeCandles = r.ChunkSizeCandles,
                chunksTotal = r.ChunksTotal,
                chunksCompleted = r.ChunksCompleted,
                lastRunStartedAtUtc = r.LastRunStartedAtUtc,
                lastRunFinishedAtUtc = r.LastRunFinishedAtUtc,
                lastSuccessAtUtc = r.LastSuccessAtUtc,
                lastError = r.LastError
            })
        });
    });

    app.MapGet("/api/admin/candle-sync/status/{timeframe}", async (
        string timeframe, HttpContext ctx, ICandleSyncRepository syncRepo) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        var row = await syncRepo.GetAsync(symbol, timeframe);
        if (row == null)
            return Results.NotFound(new { error = $"No sync state for {timeframe}" });
        return Results.Ok(new
        {
            timeframe = row.Timeframe,
            status = row.Status,
            syncMode = row.SyncMode,
            isTableEmpty = row.IsTableEmpty,
            requestedFromUtc = row.RequestedFromUtc,
            requestedToUtc = row.RequestedToUtc,
            lastExistingCandleUtc = row.LastExistingCandleUtc,
            lastSyncedCandleUtc = row.LastSyncedCandleUtc,
            offlineDurationSec = row.OfflineDurationSec,
            chunkSizeCandles = row.ChunkSizeCandles,
            chunksTotal = row.ChunksTotal,
            chunksCompleted = row.ChunksCompleted,
            lastRunStartedAtUtc = row.LastRunStartedAtUtc,
            lastRunFinishedAtUtc = row.LastRunFinishedAtUtc,
            lastSuccessAtUtc = row.LastSuccessAtUtc,
            lastError = row.LastError
        });
    });

    // ─── DB Explorer Endpoints ───────────────────────────
    var allowedTables = new HashSet<string>
{
    "candles_1m", "candles_5m", "candles_15m", "candles_30m", "candles_1h", "candles_4h",
    "ingestion_audit", "gap_events", "indicator_snapshots",
    "regime_snapshots", "signals", "signal_outcomes", "signal_features",
    "signal_decision_audit", "ml_predictions", "ml_feature_snapshots",
    "candle_sync_status"
};

    app.MapGet("/api/db/tables", () =>
    {
        return Results.Ok(allowedTables.OrderBy(t => t));
    });

    app.MapGet("/api/db/query/{tableName}", async (string tableName, int? limit) =>
    {
        if (!allowedTables.Contains(tableName))
            return Results.BadRequest(new { error = "Invalid table name" });

        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync();

        var lim = Math.Min(limit ?? 5000, 10000);
        await using var cmd = new NpgsqlCommand(
            $"SELECT * FROM \"ETH\".{tableName} ORDER BY 1 DESC LIMIT {lim}", conn);

        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await cmd.ExecuteReaderAsync();

        var columns = new List<string>();
        for (int i = 0; i < reader.FieldCount; i++)
            columns.Add(reader.GetName(i));

        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
                row[columns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        return Results.Ok(new { columns, rows, total = rows.Count });
    });

    app.MapGet("/api/db/export/{tableName}", async (string tableName) =>
    {
        if (!allowedTables.Contains(tableName))
            return Results.BadRequest("Invalid table name");

        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            $"SELECT * FROM \"ETH\".{tableName} ORDER BY 1 DESC", conn);
        await using var reader = await cmd.ExecuteReaderAsync();

        var sb = new StringBuilder();

        // Header
        var cols = new List<string>();
        for (int i = 0; i < reader.FieldCount; i++)
            cols.Add(reader.GetName(i));
        sb.AppendLine(string.Join(",", cols));

        // Rows
        while (await reader.ReadAsync())
        {
            var values = new List<string>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                if (reader.IsDBNull(i))
                    values.Add("");
                else
                {
                    var val = reader.GetValue(i).ToString() ?? "";
                    values.Add(val.Contains(',') || val.Contains('"') || val.Contains('\n')
                        ? $"\"{val.Replace("\"", "\"\"")}\""
                        : val);
                }
            }
            sb.AppendLine(string.Join(",", values));
        }

        return Results.File(Encoding.UTF8.GetBytes(sb.ToString()),
            "text/csv", $"{tableName}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv");
    });

    // ─── Playwright runtime selector probe ──────────────
    // GET /api/admin/playwright/probe-selectors
    // Runs the in-page DOM probe and returns candidate Sell/Buy elements with
    // tag/id/class/data-testid/aria-label so the operator can discover the
    // current Capital.com selectors without restarting the app or losing the
    // logged-in session. Returns 503 when the Playwright provider is not the
    // active tick source (REST-only mode).
    app.MapGet("/api/admin/playwright/probe-selectors", async (HttpContext ctx, IServiceProvider services) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        var provider = services.GetService<PlaywrightTickProvider>();
        if (provider == null)
            return Results.StatusCode(503);

        var probe = await provider.ProbeSelectorsAsync();
        if (probe == null)
            return Results.Problem("Playwright page not initialized or probe failed");

        return Results.Ok(probe);
    });

    // ─── B-07: Parameter Set Admin APIs ─────────────────
    app.MapGet("/api/admin/parameter-sets/active", async (HttpContext ctx, IParameterRepository paramRepo) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        var active = await paramRepo.GetActiveAsync(EthSignal.Domain.Models.StrategyParameters.Default.StrategyVersion);
        return active != null ? Results.Ok(active) : Results.NotFound("No active parameter set");
    });

    app.MapGet("/api/admin/parameter-sets/candidates", async (HttpContext ctx, IParameterRepository paramRepo, IParameterProvider paramProvider) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        // Issue #7: Use the active strategy version instead of a hardcoded stale version
        var activeVersion = paramProvider.GetActive().StrategyVersion;
        var candidates = await paramRepo.GetCandidatesAsync(activeVersion);
        return Results.Ok(candidates);
    });

    app.MapPost("/api/admin/parameter-sets/{id}/activate", async (long id, HttpContext ctx,
        IParameterRepository paramRepo, IParameterProvider paramProvider) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        var set = await paramRepo.GetByIdAsync(id);
        if (set == null) return Results.NotFound($"Parameter set {id} not found");

        var current = await paramRepo.GetActiveAsync(set.StrategyVersion);
        await paramRepo.ActivateAsync(id, current?.Id, "admin-api", "manual activation");
        await paramProvider.RefreshAsync();
        return Results.Ok(new { message = $"Parameter set {id} activated", previousId = current?.Id });
    });

    // ─── Adaptive Parameter System Admin APIs ──────────
    app.MapGet("/api/admin/adaptive/status", (HttpContext ctx, MarketAdaptiveParameterService adaptive, IParameterProvider paramProvider) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        var p = paramProvider.GetActive();
        return Results.Ok(adaptive.GetStatus(p));
    });

    app.MapGet("/api/admin/adaptive/conditions", (HttpContext ctx, MarketAdaptiveParameterService adaptive, IParameterProvider paramProvider) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        var status = adaptive.GetStatus(paramProvider.GetActive());
        return Results.Ok(status.ConditionDetails);
    });

    app.MapPost("/api/admin/adaptive/intensity", async (HttpContext ctx, MarketAdaptiveParameterService adaptive) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        var body = await ctx.Request.ReadFromJsonAsync<AdaptiveIntensityRequest>();
        if (body == null) return Results.BadRequest("Invalid body");
        adaptive.SetIntensity(body.Intensity);
        return Results.Ok(new { message = $"Intensity override set to {body.Intensity?.ToString("F2") ?? "cleared (using DB config)"}" });
    });

    app.MapPost("/api/admin/adaptive/enabled", async (HttpContext ctx, MarketAdaptiveParameterService adaptive) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        var body = await ctx.Request.ReadFromJsonAsync<AdaptiveEnabledRequest>();
        if (body == null) return Results.BadRequest("Invalid body");
        adaptive.SetEnabled(body.Enabled);
        return Results.Ok(new { message = $"Enabled override set to {body.Enabled?.ToString() ?? "cleared (using DB config)"}" });
    });

    // ─── B-07: Replay Admin APIs ────────────────────────
    app.MapPost("/api/admin/replay-runs", async (HttpContext ctx,
        HistoricalReplayService replay, IReplayRepository replayRepo,
        IParameterProvider paramProvider) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        var body = await ctx.Request.ReadFromJsonAsync<ReplayRunRequest>();
        if (body == null) return Results.BadRequest("Invalid body");

        var p = paramProvider.GetActive();
        var run = new ReplayRun
        {
            Symbol = body.Symbol ?? symbol,
            StartUtc = body.StartUtc,
            EndUtc = body.EndUtc,
            StrategyVersion = p.StrategyVersion,
            ParameterSetId = body.ParameterSetId,
            TriggerSource = "api"
        };
        var runId = await replayRepo.InsertRunAsync(run);

        // Run in background
        _ = Task.Run(async () =>
        {
            try
            {
                await replay.RunAsync(body.Symbol ?? symbol, body.StartUtc, body.EndUtc, p, runId);
            }
            catch { /* logged inside service */ }
        });

        return Results.Ok(new { runId, status = "queued" });
    });

    app.MapGet("/api/admin/replay-runs/{id}", async (long id, HttpContext ctx, IReplayRepository replayRepo) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        var run = await replayRepo.GetRunAsync(id);
        return run != null ? Results.Ok(run) : Results.NotFound();
    });

    // ─── B-07: Optimizer Admin APIs ─────────────────────
    app.MapPost("/api/admin/optimizer-runs", async (HttpContext ctx,
        OptimizerService optimizer, IParameterProvider paramProvider,
        IParameterRepository paramRepo, IOptimizerRepository optRepo) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        var body = await ctx.Request.ReadFromJsonAsync<OptimizerRunRequest>();
        if (body == null) return Results.BadRequest("Invalid body");

        var baseline = paramProvider.GetActive();
        var activeSet = await paramRepo.GetActiveAsync(baseline.StrategyVersion);
        var config = new OptimizerConfig
        {
            FoldCount = body.FoldCount ?? 3,
            MaxCandidates = body.MaxCandidates ?? 50,
            MinTradeCount = body.MinTradeCount ?? 10,
            MinImprovementPct = body.MinImprovementPct ?? 5.0m
        };

        // U-02 FIX: Persist optimizer_runs row immediately at creation
        var optRun = new OptimizerRun
        {
            Symbol = body.Symbol ?? symbol,
            StrategyVersion = baseline.StrategyVersion,
            BaselineParameterSetId = activeSet?.Id,
            ObjectiveFunctionVersion = baseline.ObjectiveFunctionVersion,
            StartUtc = body.StartUtc,
            EndUtc = body.EndUtc,
            Status = RunStatus.Running,
            FoldCount = config.FoldCount
        };
        var optRunId = await optRepo.InsertRunAsync(optRun);

        // Run async
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await optimizer.RunAsync(
                    body.Symbol ?? symbol, body.StartUtc, body.EndUtc, baseline, config);

                long? bestCandidateId = null;
                if (result.BestCandidate != null)
                    bestCandidateId = await optimizer.PersistBestCandidateAsync(result, "optimizer-api");

                await optRepo.UpdateRunFinishedAsync(optRunId, RunStatus.Completed,
                    bestCandidateId, result.BestCandidate?.Evaluation.FinalScore,
                    result.EvaluatedCandidates.Count, null);
            }
            catch (Exception ex)
            {
                await optRepo.UpdateRunStatusAsync(optRunId, RunStatus.Failed, ex.Message);
            }
        });

        return Results.Ok(new { runId = optRunId, status = "running" });
    });

    app.MapGet("/api/admin/optimizer-runs/{id}", async (long id, HttpContext ctx, IOptimizerRepository optRepo) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        var run = await optRepo.GetRunAsync(id);
        return run != null ? Results.Ok(run) : Results.NotFound();
    });

    // ─── ML Enhancement API Endpoints ─────────────────────────────

    // GET /api/admin/ml/models — list all ML models
    app.MapGet("/api/admin/ml/models", async (HttpContext ctx, IMlModelRepository mlModelRepo) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        var models = await mlModelRepo.GetAllAsync();
        return Results.Ok(models);
    });

    // GET /api/admin/ml/models/active — get currently active model
    app.MapGet("/api/admin/ml/models/active", async (HttpContext ctx, IMlModelRepository mlModelRepo) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        var model = await mlModelRepo.GetActiveModelAsync("outcome_predictor");
        return model != null ? Results.Ok(model) : Results.NotFound(new { message = "No active ML model" });
    });

    // GET /api/admin/ml/models/{id} — get model by ID
    app.MapGet("/api/admin/ml/models/{id}", async (long id, HttpContext ctx, IMlModelRepository mlModelRepo) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        var model = await mlModelRepo.GetByIdAsync(id);
        return model != null ? Results.Ok(model) : Results.NotFound();
    });

    // POST /api/admin/ml/models/register — register a newly trained model from Python pipeline
    // Body JSON matches the metadata JSON written by train_outcome_predictor.py
    app.MapPost("/api/admin/ml/models/register", async (HttpContext ctx, IMlModelRepository mlModelRepo) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        using var reader = new System.IO.StreamReader(ctx.Request.Body);
        var body = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(body))
            return Results.BadRequest(new { error = "Empty request body" });

        System.Text.Json.JsonDocument doc;
        try { doc = System.Text.Json.JsonDocument.Parse(body); }
        catch { return Results.BadRequest(new { error = "Invalid JSON" }); }

        var root = doc.RootElement;
        string Get(string key) => root.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";
        int GetInt(string key) => root.TryGetProperty(key, out var v) && v.TryGetInt32(out var i) ? i : 0;
        decimal GetDec(string key) => root.TryGetProperty(key, out var v) && v.TryGetDecimal(out var d) ? d : 0;

        var filePath = Get("file_path");
        var fileFormat = Get("file_format");
        var modelVersion = Get("model_version");

        if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(modelVersion))
            return Results.BadRequest(new { error = "file_path and model_version are required" });

        if (!System.IO.File.Exists(filePath))
            return Results.BadRequest(new { error = $"Model file not found: {filePath}" });

        // T3-15: Validate ONNX file format before registering.
        // An 8-byte ONNX magic check (protobuf ModelProto starts with field 1/2/3 length-delimited).
        // skl2onnx/ONNX Runtime models always begin with bytes identifying a protobuf message.
        // The simplest reliable check: attempt to load via OnnxRuntime in a throw-away session.
        var resolvedFormat = fileFormat.Length > 0 ? fileFormat.ToLowerInvariant() : "onnx";
        if (resolvedFormat == "onnx")
        {
            try
            {
                var validateOptions = new Microsoft.ML.OnnxRuntime.SessionOptions();
                validateOptions.LogSeverityLevel = Microsoft.ML.OnnxRuntime.OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR;
                using var _ = new Microsoft.ML.OnnxRuntime.InferenceSession(filePath, validateOptions);
                // Session created successfully — file is a valid ONNX model
            }
            catch (Exception onnxEx)
            {
                return Results.BadRequest(new
                {
                    error = $"File is not a valid ONNX model: {onnxEx.Message}",
                    filePath
                });
            }
        }

        var metadata = new MlModelMetadata
        {
            Id = 0,  // assigned by DB SERIAL
            ModelType = Get("model_type").Length > 0 ? Get("model_type") : "outcome_predictor",
            ModelVersion = modelVersion,
            FilePath = filePath,
            FileFormat = fileFormat.Length > 0 ? fileFormat : "onnx",
            TrainStartUtc = DateTimeOffset.UtcNow,
            TrainEndUtc = DateTimeOffset.UtcNow,
            TrainingSampleCount = GetInt("training_sample_count"),
            FeatureCount = GetInt("feature_count"),
            FeatureListJson = root.TryGetProperty("feature_list", out var fl) ? fl.GetRawText() : "[]",
            FoldMetricsJson = root.TryGetProperty("fold_metrics", out var fm) ? fm.GetRawText() : "[]",
            FeatureImportanceJson = root.TryGetProperty("feature_importance", out var fi) ? fi.GetRawText() : "{}",
            AucRoc = GetDec("avg_auc_roc"),
            BrierScore = GetDec("avg_brier_score"),
            ExpectedCalibrationError = GetDec("avg_expected_calibration_error"),
            LogLoss = GetDec("avg_log_loss"),
            Status = MlModelStatus.Candidate
        };

        var id = await mlModelRepo.InsertAsync(metadata);
        return Results.Ok(new { id, message = $"Model {modelVersion} registered as Candidate (id={id})", modelVersion });
    });

    // POST /api/admin/ml/models/{id}/activate — activate a model
    app.MapPost("/api/admin/ml/models/{id}/activate", async (long id, HttpContext ctx,
        IMlModelRepository mlModelRepo, MlInferenceService inferenceService) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        var model = await mlModelRepo.GetByIdAsync(id);
        if (model == null) return Results.NotFound();
        if (model.Status is not (MlModelStatus.Candidate or MlModelStatus.Shadow))
            return Results.BadRequest(new { error = $"Model status is {model.Status}, expected Candidate or Shadow" });

        // Enforce minimum quality before activation.
        // Regime-specific specialists (scope != ALL) are trained on ~1/3 of data so
        // minimum sample gate is relaxed; global models keep the full 200 threshold.
        var isRegimeSpecific = !string.Equals(model.RegimeScope, "ALL", StringComparison.OrdinalIgnoreCase);
        var minSamples = isRegimeSpecific ? 50 : 200;
        if (model.TrainingSampleCount < minSamples)
            return Results.BadRequest(new { error = $"Insufficient training data: {model.TrainingSampleCount} samples (minimum {minSamples} required for scope={model.RegimeScope})" });
        if (model.AucRoc < 0.58m)
            return Results.BadRequest(new { error = $"AUC-ROC too low: {model.AucRoc:F3} (minimum 0.58 required)" });
        if (model.BrierScore > 0.30m)
            return Results.BadRequest(new { error = $"Brier score too high: {model.BrierScore:F3} (maximum 0.30 required)" });

        // Retire current active model for the same scope (not across scopes)
        var current = await mlModelRepo.GetActiveModelAsync(model.ModelType, model.RegimeScope);
        if (current != null)
            await mlModelRepo.UpdateStatusAsync(current.Id, MlModelStatus.Retired, "Replaced by new activation");

        await mlModelRepo.UpdateStatusAsync(id, MlModelStatus.Active);
        await inferenceService.LoadActiveModelAsync();
        return Results.Ok(new { message = $"Model {model.ModelVersion} activated", previousModel = current?.ModelVersion });
    });

    // POST /api/admin/ml/models/{id}/retire — retire a model
    app.MapPost("/api/admin/ml/models/{id}/retire", async (long id, HttpContext ctx,
        IMlModelRepository mlModelRepo) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        var model = await mlModelRepo.GetByIdAsync(id);
        if (model == null) return Results.NotFound();

        string reason = "Manual retirement via API";
        try
        {
            var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
            if (body?.TryGetValue("reason", out var r) == true) reason = r;
        }
        catch { /* use default reason */ }

        await mlModelRepo.UpdateStatusAsync(id, MlModelStatus.Retired, reason);
        return Results.Ok(new { message = $"Model {model.ModelVersion} retired", reason });
    });

    // GET /api/ml/prediction/latest — most recent ML prediction
    app.MapGet("/api/ml/prediction/latest", async (
        string? timeframe,
        string? scope,
        IMlPredictionRepository mlPredRepo,
        IParameterProvider paramProvider) =>
    {
        var resolvedTimeframe = string.IsNullOrWhiteSpace(timeframe)
            ? paramProvider.GetActive().TimeframePrimary
            : timeframe;
        var pred = await mlPredRepo.GetLatestAsync(
            symbol,
            resolvedTimeframe,
            NormalizeMlPredictionScope(scope, "actionable"));
        return pred != null ? Results.Ok(pred) : Results.NotFound(new { message = "No ML predictions yet" });
    });

    // GET /api/ml/features/latest — latest persisted feature snapshot for dashboard inspection
    app.MapGet("/api/ml/features/latest", async (
        string? timeframe,
        IParameterProvider paramProvider,
        CancellationToken ct) =>
    {
        var resolvedTimeframe = string.IsNullOrWhiteSpace(timeframe)
            ? paramProvider.GetActive().TimeframePrimary
            : timeframe;

        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT evaluation_id, timestamp_utc, feature_version, link_status, features_json
            FROM ""ETH"".ml_feature_snapshots
            WHERE symbol = @symbol
              AND timeframe = @timeframe
            ORDER BY created_at_utc DESC
            LIMIT 1;", conn);
        cmd.Parameters.AddWithValue("symbol", symbol);
        cmd.Parameters.AddWithValue("timeframe", resolvedTimeframe);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return Results.NotFound(new { message = "No ML feature snapshots yet" });

        static double? ReadNumber(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
                return null;

            return value.TryGetDouble(out var numeric) ? numeric : null;
        }

        var evaluationId = reader.GetGuid(0);
        var timestampUtc = reader.GetFieldValue<DateTimeOffset>(1);
        var featureVersion = reader.GetString(2);
        var linkStatus = reader.IsDBNull(3) ? null : reader.GetString(3);
        var featuresJson = reader.GetString(4);

        using var doc = JsonDocument.Parse(featuresJson);
        var root = doc.RootElement;
        var availableFeatureCount = root.EnumerateObject().Count();

        return Results.Ok(new
        {
            evaluationId,
            timestampUtc,
            timeframe = resolvedTimeframe,
            featureVersion,
            linkStatus,
            currentFeatureVersion = MlFeatureExtractor.FeatureVersion,
            currentFeatureCount = MlFeatureVector.FeatureNames.Count,
            availableFeatureCount,
            marketStructure = new
            {
                sessionRangePositionPct = ReadNumber(root, "session_range_position_pct"),
                distanceToPriorDayHighPct = ReadNumber(root, "distance_to_prior_day_high_pct"),
                distanceToPriorDayLowPct = ReadNumber(root, "distance_to_prior_day_low_pct"),
                distanceToSessionVwapPct = ReadNumber(root, "distance_to_session_vwap_pct"),
                rangePositionPct = ReadNumber(root, "range_position_pct"),
                distanceTo20BarHighPct = ReadNumber(root, "distance_to_20_bar_high_pct"),
                distanceTo20BarLowPct = ReadNumber(root, "distance_to_20_bar_low_pct")
            },
            volatilityRegime = new
            {
                realizedVol15m = ReadNumber(root, "realized_vol_15m"),
                realizedVol1h = ReadNumber(root, "realized_vol_1h"),
                realizedVol4h = ReadNumber(root, "realized_vol_4h"),
                volatilityCompressionFlag = ReadNumber(root, "volatility_compression_flag"),
                volatilityExpansionFlag = ReadNumber(root, "volatility_expansion_flag"),
                atrPercentileRank = ReadNumber(root, "atr_percentile_rank")
            },
            signalSaturation = new
            {
                signalsLast10Bars = ReadNumber(root, "signals_last_10_bars"),
                sameDirectionSignalsLast10 = ReadNumber(root, "same_direction_signals_last_10"),
                oppositeDirectionSignalsLast10 = ReadNumber(root, "opposite_direction_signals_last_10"),
                recentStopOutCount = ReadNumber(root, "recent_stop_out_count"),
                recentFalseBreakoutRate = ReadNumber(root, "recent_false_breakout_rate")
            },
            btcContext = new
            {
                btcRecentReturn = ReadNumber(root, "btc_recent_return"),
                btcRegimeLabel = ReadNumber(root, "btc_regime_label"),
                ethBtcRelativeStrength = ReadNumber(root, "eth_btc_relative_strength")
            }
        });
    });

    // GET /api/ml/predictions/history — recent predictions
    app.MapGet("/api/ml/predictions/history", async (
        int? hours,
        int? limit,
        string? scope,
        string? timeframe,
        IMlPredictionRepository mlPredRepo,
        IParameterProvider paramProvider) =>
    {
        var resolvedTimeframe = string.IsNullOrWhiteSpace(timeframe)
            ? paramProvider.GetActive().TimeframePrimary
            : timeframe;
        var preds = await mlPredRepo.GetRecentAsync(
            symbol,
            hours ?? 24,
            limit ?? 100,
            resolvedTimeframe,
            NormalizeMlPredictionScope(scope, "actionable"));
        return Results.Ok(preds);
    });

    // GET /api/ml/performance — ML model performance metrics
    app.MapGet("/api/ml/performance", (MlDriftDetector driftDetector, IParameterProvider pp, MlInferenceService inferenceService) =>
    {
        var p = pp.GetActive();
        var modelVersion = inferenceService.ActiveModelVersion ?? "none";
        var drift = driftDetector.CheckDrift(p, modelVersion);

        return Results.Ok(new
        {
            modelVersion,
            isReady = inferenceService.IsReady,
            mlMode = p.MlMode.ToString(),
            drift = new
            {
                drift.DriftDetected,
                drift.RollingAuc,
                drift.RollingBrier,
                drift.ActualWinRate,
                drift.PredictedMeanWin,
                drift.WindowSize
            }
        });
    });

    // GET /api/admin/ml/config — current ML parameters
    app.MapGet("/api/admin/ml/config", (HttpContext ctx, IParameterProvider pp) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        var p = pp.GetActive();
        return Results.Ok(new
        {
            p.MlMode,
            p.MlMinWinProbability,
            p.MlConfidenceBlendWeight,
            p.MlDynamicThresholdsEnabled,
            p.MlDynamicThresholdMin,
            p.MlDynamicThresholdMax,
            p.MlDynamicThresholdMaxDelta,
            p.MlRetrainSignalThreshold,
            p.MlRetrainMaxDays,
            p.MlShadowEvalCount,
            p.MlDriftAucThreshold,
            p.MlDriftBrierThreshold,
            p.MlOverrideMandatoryGates
        });
    });

    // POST /api/admin/ml/mode — switch ML mode
    app.MapPost("/api/admin/ml/mode", async (HttpContext ctx, IParameterProvider pp, IParameterRepository paramRepo) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
        if (body == null || !body.TryGetValue("mode", out var modeStr))
            return Results.BadRequest(new { error = "Missing 'mode' in request body" });

        if (!Enum.TryParse<MlMode>(modeStr, ignoreCase: true, out var newMode))
            return Results.BadRequest(new { error = $"Invalid mode: {modeStr}. Must be DISABLED, SHADOW, or ACTIVE" });

        var currentParams = pp.GetActive();
        var updatedParams = currentParams with { MlMode = newMode };

        // Save as a new parameter set
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(updatedParams.ToJson()));
        var hashStr = Convert.ToHexString(hash)[..16].ToLowerInvariant();

        var setId = await paramRepo.InsertAsync(new StrategyParameterSet
        {
            StrategyVersion = updatedParams.StrategyVersion,
            ParameterHash = hashStr,
            Parameters = updatedParams,
            Status = ParameterSetStatus.Active,
            CreatedBy = "ml-mode-switch",
            Notes = $"ML mode changed to {newMode}"
        });
        await paramRepo.ActivateAsync(setId, null, "ml-mode-switch", $"ML mode → {newMode}");
        await pp.RefreshAsync();

        return Results.Ok(new { mode = newMode.ToString(), parameterSetId = setId });
    });

    // GET /api/admin/ml/drift — recent drift events
    app.MapGet("/api/admin/ml/drift", (HttpContext ctx, MlDriftDetector driftDetector, IParameterProvider pp, MlInferenceService inferenceService) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        var p = pp.GetActive();
        var modelVersion = inferenceService.ActiveModelVersion ?? "none";
        var result = driftDetector.CheckDrift(p, modelVersion);
        return Results.Ok(result);
    });

    // GET /api/admin/ml/health — ML system health
    app.MapGet("/api/admin/ml/health", async (
        HttpContext ctx,
        MlInferenceService inferenceService,
        MlModelPromotionService promotionService,
        MlDriftDetector driftDetector,
        IParameterProvider pp,
        IMlDataDiagnosticsService diagnosticsService,
        CancellationToken ct) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        var p = pp.GetActive();
        var modelVersion = inferenceService.ActiveModelVersion ?? "none";
        var drift = driftDetector.CheckDrift(p, modelVersion);
        var diagnostics = await diagnosticsService.GetReportAsync(
            symbol,
            "all",
            p.MlMinWinProbability,
            ct);

        var healthReason =
            diagnostics.FeatureDrift.Status == MlDiagnosticsStatus.Critical ? "Feature drift" :
            diagnostics.Calibration.Status == MlDiagnosticsStatus.Critical ? "Calibration" :
            diagnostics.LabelQuality.Status == MlDiagnosticsStatus.Critical ? "Label quality" :
            diagnostics.ClassBalance.Status == MlDiagnosticsStatus.Critical ? "Class balance" :
            diagnostics.FeatureDrift.Status == MlDiagnosticsStatus.Warning ? "Feature drift warning" :
            diagnostics.Calibration.Status == MlDiagnosticsStatus.Warning ? "Calibration warning" :
            diagnostics.LabelQuality.Status == MlDiagnosticsStatus.Warning ? "Label quality warning" :
            diagnostics.ClassBalance.Status == MlDiagnosticsStatus.Warning ? "Class balance warning" :
            diagnostics.OverallStatus == MlDiagnosticsStatus.InsufficientData ? "Insufficient data" :
            "Healthy";

        return Results.Ok(new
        {
            modelLoaded = inferenceService.IsReady,
            modelVersion,
            modelFormat = inferenceService.ActiveModelFormat ?? "none",
            isHeuristicFallback = inferenceService.IsHeuristicFallback,
            promotionBlockReason = promotionService.LastPromotionBlockReason,
            mlMode = p.MlMode.ToString(),
            overallStatus = diagnostics.OverallStatus,
            healthReason,
            labelQualityStatus = diagnostics.LabelQuality.Status,
            classBalanceStatus = diagnostics.ClassBalance.Status,
            calibrationStatus = diagnostics.Calibration.Status,
            featureDriftStatus = diagnostics.FeatureDrift.Status,
            featureVersion = diagnostics.FeatureVersion,
            isFeatureVersionFallback = diagnostics.IsFeatureVersionFallback,
            trainableFeatureSnapshots = diagnostics.LabelQuality.TrainableFeatureSnapshots,
            labeledFeatureSnapshots = diagnostics.LabelQuality.LabeledFeatureSnapshots,
            diagnosticsGeneratedAtUtc = diagnostics.GeneratedAtUtc,
            driftDetected = drift.DriftDetected || diagnostics.FeatureDrift.Status == MlDiagnosticsStatus.Critical,
            rollingAuc = drift.RollingAuc,
            rollingBrier = drift.RollingBrier,
            windowSize = drift.WindowSize
        });
    });

    // GET /api/admin/ml/diagnostics — training-data quality, calibration, and feature drift
    app.MapGet("/api/admin/ml/diagnostics", async (
        HttpContext ctx,
        IMlDataDiagnosticsService diagnosticsService,
        IParameterProvider pp,
        CancellationToken ct) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        var p = pp.GetActive();
        var report = await diagnosticsService.GetReportAsync(
            symbol,
            "all",
            p.MlMinWinProbability,
            ct);
        return Results.Ok(report);
    });

    // GET /api/admin/ml/training/status — auto-trainer readiness + recent runs
    app.MapGet("/api/admin/ml/training/status", async (
        HttpContext ctx,
        MlTrainingState state,
        IMlTrainingRunRepository trainingRepo,
        IParameterProvider pp,
        CancellationToken ct) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        var runs = await trainingRepo.GetRecentAsync(10, ct);
        var lastSuccess = await trainingRepo.GetLatestSuccessfulAsync(ct);
        var p = pp.GetActive();
        var nextAt = lastSuccess?.FinishedAtUtc?.AddDays(p.MlRetrainMaxDays);

        return Results.Ok(new
        {
            isRunning = state.IsRunning,
            currentRunId = state.CurrentRunId,
            currentTrigger = state.CurrentTrigger,
            runStartedAt = state.RunStartedAt,
            lastStatus = state.LastStatus,
            lastDuration = state.LastDuration?.TotalSeconds,
            lastResultModelId = state.LastResultModelId,
            lastCompletedAt = state.LastCompletedAt,
            labeledSamples = state.LabeledSamples,
            trainableSamples = state.LabeledSamples,
            wins = state.Wins,
            losses = state.Losses,
            readinessBasis = "export-aligned trainable WIN/LOSS rows",
            minSamplesRequired = 200,
            minSamplesPerClass = 30,
            retrainThreshold = p.MlRetrainSignalThreshold,
            retrainMaxDays = p.MlRetrainMaxDays,
            lastSuccessAt = lastSuccess?.FinishedAtUtc,
            nextScheduledAt = nextAt,
            recentRuns = runs.Select(r => new
            {
                r.Id,
                r.Trigger,
                r.Status,
                r.SampleCount,
                r.StartedAtUtc,
                r.FinishedAtUtc,
                r.DurationSeconds,
                r.ErrorText
            })
        });
    });

    // POST /api/admin/ml/training/trigger — manual training kick-off (localhost only)
    app.MapPost("/api/admin/ml/training/trigger", (
        MlTrainingService trainingService,
        MlTrainingState state,
        HttpContext http) =>
    {
        if (RejectIfNotLoopback(http) is { } forbidden)
            return forbidden;

        if (state.IsRunning)
            return Results.Conflict(new { error = "Training already in progress", runId = state.CurrentRunId });

        _ = trainingService.TriggerManualAsync(CancellationToken.None);
        return Results.Accepted(value: new { message = "Training triggered", trigger = "manual" });
    });

    // Run DB migration early so all tables/columns exist before startup logic
    {
        var migrator = app.Services.GetRequiredService<IDbMigrator>();
        await migrator.MigrateAsync();
        Log.Information("Database migration completed");
    }

    if (app.Environment.IsEnvironment("Testing"))
    {
        Log.Information("Skipping startup warmup for Testing environment");
    }
    else
    {
        // Seed the default active parameter set if none exists, then refresh cache.
        // StrategyParameters.Default keeps MlMode=SHADOW so first-run behavior remains safe.
        {
            var paramRepo = app.Services.GetRequiredService<IParameterRepository>();
            var pp = app.Services.GetRequiredService<IParameterProvider>();

            var active = await paramRepo.GetActiveAsync(EthSignal.Domain.Models.StrategyParameters.Default.StrategyVersion);
            if (active == null)
            {
                var defaults = EthSignal.Domain.Models.StrategyParameters.Default;
                var hash = System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(defaults.ToJson()));
                var hashStr = Convert.ToHexString(hash)[..16].ToLowerInvariant();

                var setId = await paramRepo.InsertAsync(new EthSignal.Domain.Models.StrategyParameterSet
                {
                    StrategyVersion = defaults.StrategyVersion,
                    ParameterHash = hashStr,
                    Parameters = defaults,
                    Status = EthSignal.Domain.Models.ParameterSetStatus.Active,
                    CreatedBy = "startup-seed",
                    Notes = "Auto-seeded default parameter set"
                });
                await paramRepo.ActivateAsync(setId, null, "startup-seed", "initial default seeding");
                Log.Information("Seeded default parameter set id={Id} hash={Hash} mlMode={MlMode}", setId, hashStr, defaults.MlMode);
            }
            else
            {
                Log.Information("Active parameter set found: id={Id} hash={Hash} version={Version}",
                    active.Id, active.ParameterHash, active.StrategyVersion);
            }

            await pp.RefreshAsync();
            var loaded = pp.GetActive();
            Log.Information("ParameterProvider loaded: version={Version} warmup={WarmUp} adxThreshold={Adx}",
                loaded.StrategyVersion, loaded.WarmUpPeriod, loaded.AdxTrendThreshold);
        }

        // Issue #3: rehydrate persisted adaptive state so retrospective overlays survive restart
        {
            var adaptive = app.Services.GetRequiredService<MarketAdaptiveParameterService>();
            await adaptive.LoadStateAsync();
        }

        // ML: Auto-promote the strongest validated candidate before inference loads
        try
        {
            var mlPromotion = app.Services.GetRequiredService<MlModelPromotionService>();
            var mlInference = app.Services.GetRequiredService<MlInferenceService>();
            mlInference.SetParameterProvider(app.Services.GetRequiredService<IParameterProvider>());
            mlInference.SetFrequencyManager(app.Services.GetRequiredService<SignalFrequencyManager>());

            // Build the accuracy-first promotion context from current diagnostics
            // so drift / calibration / class-balance can block promotion.
            MlPromotionContext? promotionContext = null;
            try
            {
                var diagSvcStartup = app.Services.GetRequiredService<IMlDataDiagnosticsService>();
                var ppStartup = app.Services.GetRequiredService<IParameterProvider>();
                var pStartup = ppStartup.GetActive();
                var diagStartup = await diagSvcStartup.GetReportAsync(
                    symbol, "all", pStartup.MlMinWinProbability);
                promotionContext = new MlPromotionContext
                {
                    FeatureDriftStatus = diagStartup.FeatureDrift.Status,
                    LabeledSamples = diagStartup.ClassBalance.LabeledSamples,
                    Wins = diagStartup.ClassBalance.Wins,
                    Losses = diagStartup.ClassBalance.Losses,
                    CalibrationSampleCount = diagStartup.Calibration.SampleCount,
                    CalibrationBrier = diagStartup.Calibration.BrierScore > 0m
                        ? diagStartup.Calibration.BrierScore
                        : null,
                    ThresholdLift = diagStartup.Calibration.ThresholdLift
                };
            }
            catch (Exception diagEx)
            {
                Log.Warning(diagEx,
                    "Startup: promotion context diagnostics lookup failed — proceeding with metadata-only gates");
            }

            var promotion = await mlPromotion.PromoteBestModelAsync("outcome_predictor", CancellationToken.None, promotionContext);
            if (promotion.Activated && promotion.SelectedModel != null)
            {
                Log.Information(
                    "Startup: auto-promoted ML model {Version} (AUC={Auc:F4} Brier={Brier:F4} Samples={Samples}) | Reason={Reason}",
                    promotion.SelectedModel.ModelVersion,
                    promotion.SelectedModel.AucRoc,
                    promotion.SelectedModel.BrierScore,
                    promotion.SelectedModel.TrainingSampleCount,
                    promotion.Reason);
            }
            else
            {
                Log.Information("Startup: ML promotion check kept current model ({Reason})", promotion.Reason);
            }

            await mlInference.LoadActiveModelAsync();
            if (mlInference.IsReady && !mlInference.IsHeuristicFallback)
            {
                Log.Information("ML model loaded: {Version} ({Format})",
                    mlInference.ActiveModelVersion, mlInference.ActiveModelFormat);

                // Optional startup auto-activation: only switch MlMode to ACTIVE when
                // explicitly enabled in parameters. SHADOW remains the default.
                // Accuracy-first gate: heuristic fallback must NEVER auto-activate —
                // it is only used in SHADOW/annotation mode.
                try
                {
                    var autoPp = app.Services.GetRequiredService<IParameterProvider>();
                    var autoParamRepo = app.Services.GetRequiredService<IParameterRepository>();
                    var currentParams = autoPp.GetActive();
                    if (!currentParams.MlAutoActivateOnStartup)
                    {
                        Log.Information(
                            "Startup: MlMode remains {Mode} because MlAutoActivateOnStartup is disabled by default",
                            currentParams.MlMode);
                    }
                    else if (currentParams.MlMode != MlMode.ACTIVE)
                    {
                        var updatedParams = currentParams with { MlMode = MlMode.ACTIVE };
                        var autoHash = System.Security.Cryptography.SHA256.HashData(
                            System.Text.Encoding.UTF8.GetBytes(updatedParams.ToJson()));
                        var autoHashStr = Convert.ToHexString(autoHash)[..16].ToLowerInvariant();

                        var autoSetId = await autoParamRepo.InsertAsync(new StrategyParameterSet
                        {
                            StrategyVersion = updatedParams.StrategyVersion,
                            ParameterHash = autoHashStr,
                            Parameters = updatedParams,
                            Status = ParameterSetStatus.Active,
                            CreatedBy = "startup-ml-auto-activate",
                            Notes = $"Auto-switched MlMode {currentParams.MlMode} → ACTIVE (healthy model {mlInference.ActiveModelVersion} loaded at startup)"
                        });
                        await autoParamRepo.ActivateAsync(autoSetId, null, "startup-ml-auto-activate",
                            $"ML auto-activated (model {mlInference.ActiveModelVersion})");
                        await autoPp.RefreshAsync();
                        Log.Information(
                            "Startup: auto-switched MlMode {Previous} → ACTIVE (model {Version} healthy)",
                            currentParams.MlMode, mlInference.ActiveModelVersion);
                    }
                    else
                    {
                        Log.Information("Startup: MlMode already ACTIVE — no switch required");
                    }
                }
                catch (Exception autoEx)
                {
                    Log.Warning(autoEx, "Startup: ML auto-activation failed (non-fatal) — current MlMode retained");
                }
            }
            else if (mlInference.IsHeuristicFallback)
            {
                Log.Information(
                    "Heuristic ML fallback active — not auto-switching to ACTIVE. " +
                    "Train and register a real ONNX model before enabling ACTIVE mode.");
            }
            else
            {
                Log.Information("No active ML model — running in rule-based mode");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ML model loading failed (non-fatal) — running in rule-based mode");
        }
    }

    // U-04: Auto-resolve old gaps on startup so historical gaps don't block live processing
    try
    {
        var auditRepo = app.Services.GetRequiredService<IAuditRepository>();
        var resolved = await auditRepo.ResolveOldGapsAsync(symbol, maxAgeMinutes: 120);
        if (resolved > 0)
            Log.Information("Startup: auto-resolved {Count} old gaps for {Symbol}", resolved, symbol);

        var diag = await auditRepo.GetGapDiagnosticsAsync(symbol);
        Log.Information("Gap diagnostics: {RecentUnresolved} recent unresolved, {TotalUnresolved} total unresolved",
            diag.UnresolvedRecentCount, diag.UnresolvedTotalCount);
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Startup gap resolution failed (non-fatal) — will retry at runtime");
    }

    // U-04: Gap resolution and diagnostics endpoints
    app.MapPost("/api/admin/resolve-old-gaps", async (HttpContext ctx, IAuditRepository auditRepo) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        var resolved = await auditRepo.ResolveOldGapsAsync(symbol);
        if (resolved > 0)
            Log.Information("Resolved {Count} old gaps for {Symbol}", resolved, symbol);
        return Results.Ok(new { resolved, symbol });
    });

    app.MapGet("/api/admin/gap-diagnostics", async (HttpContext ctx, IAuditRepository auditRepo) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        var diag = await auditRepo.GetGapDiagnosticsAsync(symbol);
        return Results.Ok(diag);
    });

    // U-05: OHLC repair endpoint — scans and fixes invalid candles
    app.MapPost("/api/admin/repair-ohlc", async (HttpContext ctx, ICandleRepository candleRepo) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        int totalRepaired = 0;
        foreach (var tf in Timeframe.All)
        {
            var repaired = await candleRepo.RepairInvalidOhlcAsync(tf, symbol);
            if (repaired > 0)
                Log.Information("Repaired {Count} invalid OHLC rows in {Table}", repaired, tf.Table);
            totalRepaired += repaired;
        }
        return Results.Ok(new { totalRepaired, tables = Timeframe.All.Select(t => t.Name) });
    });

    var truncateApiKey = builder.Configuration["TRUNCATE_API_KEY"] ?? "";

    app.MapPost("/api/db/truncate-all", async (HttpContext ctx) =>
    {
        // W-04: If no API key is configured, disable endpoint entirely
        if (string.IsNullOrWhiteSpace(truncateApiKey))
            return Results.NotFound();

        // W-04: Require API key as Bearer token or X-Api-Key header
        var bearerToken = ctx.Request.Headers["Authorization"].FirstOrDefault()?.Replace("Bearer ", "");
        var xApiKey = ctx.Request.Headers["X-Api-Key"].FirstOrDefault();
        if (bearerToken != truncateApiKey && xApiKey != truncateApiKey)
            return Results.Unauthorized();

        // P8-06: Restrict to localhost only
        var remoteIp = ctx.Connection.RemoteIpAddress;
        if (remoteIp != null && !System.Net.IPAddress.IsLoopback(remoteIp))
            return Results.StatusCode(403);

        // Require confirmation header
        if (ctx.Request.Headers["X-Confirm-Truncate"].FirstOrDefault() != "YES")
            return Results.BadRequest(new { error = "Missing X-Confirm-Truncate: YES header" });

        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync();

        foreach (var table in allowedTables)
        {
            await using var cmd = new NpgsqlCommand(
                $"TRUNCATE TABLE \"ETH\".{table} CASCADE", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        return Results.Ok(new { message = "All tables truncated", tables = allowedTables.Count });
    });


    // ── Playwright headless toggle ─────────────────────────────────────────────
    app.MapGet("/api/admin/playwright/headless", (HttpContext ctx, IConfiguration config, IServiceProvider services) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        var provider = services.GetService<PlaywrightTickProvider>();
        return Results.Ok(new
        {
            headless = provider?.Headless ?? config.GetValue<bool>("HighFreqTicks:Headless", true),
            appliedLive = provider != null
        });
    });

    app.MapPost("/api/admin/playwright/headless", async (HttpContext ctx, IHostEnvironment env, IServiceProvider services) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, JsonElement>>();
        if (body == null || !body.TryGetValue("headless", out var el) ||
            (el.ValueKind != JsonValueKind.True && el.ValueKind != JsonValueKind.False))
            return Results.BadRequest(new { error = "Missing 'headless' bool in body" });

        bool headless = el.GetBoolean();
        var settingsPath = Path.Combine(env.ContentRootPath, "appsettings.json");
        var raw = await File.ReadAllTextAsync(settingsPath);
        var doc = System.Text.Json.Nodes.JsonNode.Parse(raw)!.AsObject();
        doc["HighFreqTicks"] ??= new System.Text.Json.Nodes.JsonObject();
        doc["HighFreqTicks"]!["Headless"] = headless;
        await File.WriteAllTextAsync(settingsPath,
            doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var provider = services.GetService<PlaywrightTickProvider>();
        var appliedLive = false;
        if (provider != null)
        {
            await provider.SetHeadlessModeAsync(headless, ctx.RequestAborted);
            appliedLive = true;
        }

        return Results.Ok(new { headless, appliedLive, persisted = true });
    });

    // ── Signal blocker controls (portal_overrides table) ───────────────────────
    // GET returns the raw portal overrides (null fields = not overridden, base param set is used).
    app.MapGet("/api/admin/signal-blockers", async (HttpContext ctx, IPortalOverridesRepository overridesRepo) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        var o = await overridesRepo.GetAsync();
        return Results.Ok(new
        {
            maxOpenPositions = o?.MaxOpenPositions,
            maxOpenPerTimeframe = o?.MaxOpenPerTimeframe,
            maxOpenPerDirection = o?.MaxOpenPerDirection,
            dailyLossCapPercent = o?.DailyLossCapPercent,
            maxConsecutiveLossesPerDay = o?.MaxConsecutiveLossesPerDay,
            scalpMaxConsecutiveLossesPerDay = o?.ScalpMaxConsecutiveLossesPerDay,
            updatedAt = o?.UpdatedAt,
            updatedBy = o?.UpdatedBy
        });
    });

    app.MapPatch("/api/admin/signal-blockers", async (HttpContext ctx,
        IPortalOverridesRepository overridesRepo, IParameterProvider pp) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, JsonElement>>();
        if (body == null) return Results.BadRequest(new { error = "Missing request body" });

        int? GetInt(string key) => body.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : (int?)null;
        decimal? GetDec(string key) => body.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDecimal() : (decimal?)null;

        var overrides = new PortalOverrides
        {
            MaxOpenPositions = GetInt("maxOpenPositions"),
            MaxOpenPerTimeframe = GetInt("maxOpenPerTimeframe"),
            MaxOpenPerDirection = GetInt("maxOpenPerDirection"),
            DailyLossCapPercent = GetDec("dailyLossCapPercent"),
            MaxConsecutiveLossesPerDay = GetInt("maxConsecutiveLossesPerDay"),
            ScalpMaxConsecutiveLossesPerDay = GetInt("scalpMaxConsecutiveLossesPerDay"),
            UpdatedBy = "portal"
        };

        await overridesRepo.SaveAsync(overrides);
        await pp.RefreshAsync(); // apply immediately if service is running

        return Results.Ok(new
        {
            maxOpenPositions = overrides.MaxOpenPositions,
            maxOpenPerTimeframe = overrides.MaxOpenPerTimeframe,
            maxOpenPerDirection = overrides.MaxOpenPerDirection,
            dailyLossCapPercent = overrides.DailyLossCapPercent,
            maxConsecutiveLossesPerDay = overrides.MaxConsecutiveLossesPerDay,
            scalpMaxConsecutiveLossesPerDay = overrides.ScalpMaxConsecutiveLossesPerDay
        });
    });

    app.MapPost("/api/admin/signal-blockers/refresh", async (HttpContext ctx, IParameterProvider pp) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        var refreshed = await pp.RefreshAsync(ctx.RequestAborted);
        var current = pp.GetActive();

        return Results.Ok(new
        {
            refreshed,
            current.MaxOpenPositions,
            current.MaxOpenPerTimeframe,
            current.MaxOpenPerDirection,
            current.DailyLossCapPercent,
            current.MaxConsecutiveLossesPerDay,
            current.ScalpMaxConsecutiveLossesPerDay
        });
    });

    // ── Global configuration controls (portal_overrides table) ─────────────────
    // Recommended-signal execution is enforced only in the auto-executor path so
    // signal generation, dashboards, and manual execution remain unaffected.
    app.MapGet("/api/admin/global-config", async (HttpContext ctx, IPortalOverridesRepository overridesRepo) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        var o = await overridesRepo.GetAsync();
        var effective = o?.RecommendedSignalExecutionEnabled ?? true;
        Log.Information("Global config loaded: RecommendedSignalExecutionEnabled={Enabled} overridden={Overridden}",
            effective, o?.RecommendedSignalExecutionEnabled.HasValue == true);
        return Results.Ok(new
        {
            recommendedSignalExecutionEnabled = effective,
            overridden = o?.RecommendedSignalExecutionEnabled.HasValue == true,
            updatedAt = o?.UpdatedAt,
            updatedBy = o?.UpdatedBy
        });
    });

    app.MapPatch("/api/admin/global-config", async (HttpContext ctx, IPortalOverridesRepository overridesRepo) =>
    {
        if (RejectIfNotLoopback(ctx) is { } forbidden)
            return forbidden;

        var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, JsonElement>>();
        if (body == null) return Results.BadRequest(new { error = "Missing request body" });
        if (!body.TryGetValue("recommendedSignalExecutionEnabled", out var toggleElement) ||
            toggleElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return Results.BadRequest(new { error = "Missing 'recommendedSignalExecutionEnabled' bool in body" });
        }

        var enabled = toggleElement.GetBoolean();
        await overridesRepo.SaveAsync(new PortalOverrides
        {
            RecommendedSignalExecutionEnabled = enabled,
            UpdatedBy = "portal"
        });

        Log.Information("Global config changed: RecommendedSignalExecutionEnabled={Enabled}", enabled);

        return Results.Ok(new
        {
            recommendedSignalExecutionEnabled = enabled,
            updatedBy = "portal"
        });
    });

    // ── SSE: live tick stream ───────────────────────────────────────────────────
    app.MapGet("/api/ticks/stream", async (
        MarketStateCache cache,
        HttpContext httpContext,
        CancellationToken ct) =>
    {
        var resp = httpContext.Response;
        resp.ContentType = "text/event-stream";
        resp.Headers.CacheControl = "no-cache";
        resp.Headers.Connection = "keep-alive";

        var writer = resp.BodyWriter;

        while (!ct.IsCancellationRequested)
        {
            var spot = cache.LatestSpot;
            if (spot != null)
            {
                var json = $"{{\"bid\":{spot.Bid},\"ask\":{spot.Ask}," +
                           $"\"mid\":{spot.Mid},\"ts\":\"{spot.Timestamp:O}\"}}";
                var line = System.Text.Encoding.UTF8.GetBytes($"data: {json}\n\n");
                await writer.WriteAsync(line, ct);
                await writer.FlushAsync(ct);
            }
            await Task.Delay(100, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    });

    // ── Health endpoint: tick metrics ───────────────────────────────────────────
    app.MapGet("/api/health/ticks", (MarketStateCache cache) =>
    {
        var h = cache.GetHealthInfo();
        return Results.Ok(new
        {
            h.TickRateHz,
            h.TickProviderKind,
            h.LastTickTime,
            h.TickCount,
            h.LastCandleCloseTime,
            h.LastCandleTimeframe,
            h.LastError
        });
    });

    app.Run();

}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>Enables WebApplicationFactory in integration tests.</summary>
public partial class Program { }

// ─── B-07: Request DTOs ─────────────────────────────
record ReplayRunRequest(string? Symbol, DateTimeOffset StartUtc, DateTimeOffset EndUtc, long? ParameterSetId);
record OptimizerRunRequest(string? Symbol, DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    int? FoldCount, int? MaxCandidates, int? MinTradeCount, decimal? MinImprovementPct);

// ─── Adaptive Admin Request DTOs ─────────────────────
record AdaptiveIntensityRequest(decimal? Intensity);
record AdaptiveEnabledRequest(bool? Enabled);
