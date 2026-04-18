using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Apis;
using EthSignal.Infrastructure.Db;
using EthSignal.Infrastructure.Engine;
using EthSignal.Infrastructure.Notifications;

namespace EthSignal.Web.BackgroundServices;

public sealed class DataIngestionService : BackgroundService
{
    private readonly ICapitalClient _api;
    private readonly IDbMigrator _migrator;
    private readonly BackfillService _backfiller;
    private readonly HistoricalCandleSyncService _historicalSync;
    private readonly CandleSyncState _candleSyncState;
    private readonly LiveTickProcessor _liveProcessor;
    private readonly HistoricalReplayService _replayService;
    private readonly IDecisionAuditRepository _decisionAuditRepo;
    private readonly IParameterProvider _paramProvider;
    private readonly IConfiguration _config;
    private readonly ILogger<DataIngestionService> _logger;
    private readonly ITelegramNotifier _telegram;

    public DataIngestionService(
        ICapitalClient api,
        IDbMigrator migrator,
        BackfillService backfiller,
        HistoricalCandleSyncService historicalSync,
        CandleSyncState candleSyncState,
        LiveTickProcessor liveProcessor,
        HistoricalReplayService replayService,
        IDecisionAuditRepository decisionAuditRepo,
        IParameterProvider paramProvider,
        IConfiguration config,
        ILogger<DataIngestionService> logger,
        ITelegramNotifier telegram)
    {
        _api = api;
        _migrator = migrator;
        _backfiller = backfiller;
        _historicalSync = historicalSync;
        _candleSyncState = candleSyncState;
        _liveProcessor = liveProcessor;
        _replayService = replayService;
        _decisionAuditRepo = decisionAuditRepo;
        _paramProvider = paramProvider;
        _config = config;
        _logger = logger;
        _telegram = telegram;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var symbol = _config["CapitalApi:Symbol"] ?? "ETHUSD";
        var epic = _config["CapitalApi:Epic"] ?? symbol; // Capital.com API identifier
        var backfillDays = int.TryParse(_config["CapitalApi:BackfillDays"], out var d) ? d : 7;
        var uiPriceOnly = _config.GetValue<bool>("HighFreqTicks:UiPriceOnly", true);

        try
        {
            // 1. Database migration
            _logger.LogInformation("Initializing database...");
            await _migrator.MigrateAsync(stoppingToken);
            _logger.LogInformation("Database initialized OK");

            // Notify: system started
            _ = _telegram.SendAsync(
                TelegramMessageFormatter.SystemStart(symbol, "Production"),
                stoppingToken);

            // 2. Authenticate if historical sync is enabled (decoupled from UiPriceOnly).
            //    UiPriceOnly continues to control live tick acquisition only.
            if (_historicalSync.IsEnabled)
            {
                await AuthenticateWithRetryAsync(stoppingToken);
            }
            else
            {
                _logger.LogInformation(
                    "CapitalApi:HistoricalSyncEnabled = false — skipping startup historical candle sync");
            }

            // 3. Startup historical candle sync (per-timeframe empty bootstrap or
            //    offline gap recovery). Must complete before LiveTickProcessor starts.
            if (_historicalSync.IsEnabled)
            {
                var summary = await _historicalSync.RunAsync(symbol, epic, stoppingToken);
                _candleSyncState.Set(summary);

                if (summary.FailedTimeframes > 0)
                {
                    _logger.LogCritical(
                        "Startup historical candle sync FAILED ({Failed}/{Total}) — refusing to start live processing",
                        summary.FailedTimeframes, summary.TotalTimeframes);
                    throw new InvalidOperationException(
                        $"Startup historical candle sync failed for {summary.FailedTimeframes}/{summary.TotalTimeframes} timeframes");
                }
            }

            // 3b. Legacy 1m-anchored backfill is now redundant for fresh installs.
            //     We keep it available for non-UiPriceOnly setups that still want
            //     the historical replay path it feeds, but only when the operator
            //     explicitly opts in via CapitalApi:LegacyBackfillEnabled = true.
            var legacyBackfillEnabled = _config.GetValue("CapitalApi:LegacyBackfillEnabled", defaultValue: false);
            if (!uiPriceOnly && legacyBackfillEnabled)
            {
                _logger.LogInformation(
                    "Legacy 1m-anchored backfill enabled — running BackfillService for {Symbol} ({Days} days)",
                    symbol, backfillDays);
                await _backfiller.BackfillAsync(symbol, epic, backfillDays, stoppingToken);
            }

            // TF-6: Optional historical replay mode after backfill
            var p = _paramProvider.GetActive();
            if (p.BackfillReplaySignals)
            {
                _logger.LogInformation("BackfillReplaySignals enabled — running historical replay for {Symbol}", symbol);
                try
                {
                    var replayFrom = DateTimeOffset.UtcNow.AddDays(-backfillDays);
                    var replayTo = DateTimeOffset.UtcNow;
                    var replayResult = await _replayService.RunInMemoryAsync(
                        symbol, replayFrom, replayTo, p, stoppingToken);

                    // Persist replay decisions (tagged as HISTORICAL_REPLAY)
                    int replayDecisionCount = 0;
                    foreach (var sig in replayResult.Signals)
                    {
                        var snap = new Dictionary<string, decimal>
                        {
                            ["entry_price"] = sig.EntryPrice,
                            ["confidence"] = sig.ConfidenceScore
                        };
                        var replayDecision = new SignalDecision
                        {
                            Symbol = sig.Symbol,
                            Timeframe = sig.Timeframe,
                            DecisionTimeUtc = sig.SignalTimeUtc,
                            BarTimeUtc = sig.SignalTimeUtc - Timeframe.ByName(sig.Timeframe).Duration,
                            DecisionType = sig.Direction,
                            OutcomeCategory = sig.Direction != SignalDirection.NO_TRADE
                                ? OutcomeCategory.SIGNAL_GENERATED
                                : OutcomeCategory.STRATEGY_NO_TRADE,
                            UsedRegime = sig.Regime,
                            UsedRegimeTimestamp = null,
                            ReasonCodes = [],
                            ReasonDetails = sig.Reasons.ToList(),
                            ConfidenceScore = sig.ConfidenceScore,
                            IndicatorSnapshot = snap,
                            ParameterSetId = p.StrategyVersion,
                            SourceMode = SourceMode.HISTORICAL_REPLAY
                        };
                        if (await _decisionAuditRepo.InsertDecisionAsync(replayDecision, stoppingToken))
                            replayDecisionCount++;
                    }

                    _logger.LogInformation(
                        "Historical replay completed: {Signals} signals, {Decisions} decision audit rows persisted",
                        replayResult.Signals.Count, replayDecisionCount);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Historical replay failed (non-fatal) — continuing to live mode");
                }
            }

            // 4. Live tick loop
            _logger.LogInformation("Starting live tick processor for {Symbol}", symbol);
            await _liveProcessor.RunAsync(symbol, epic, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Data ingestion service stopping.");
            await _telegram.SendAsync(
            TelegramMessageFormatter.SystemStop(symbol, "Data ingestion service stopping."));
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Data ingestion service failed.");
            try
            {
                await _telegram.SendAsync(
                TelegramMessageFormatter.SystemStop(symbol, ex.Message));
            }
            catch { /* best-effort */ }
            throw;
        }
        finally
        {
            try
            {
                await _telegram.SendAsync(
                TelegramMessageFormatter.SystemStop(symbol, "Graceful shutdown"));
            }
            catch { /* best-effort */ }
        }
    }

    private async Task AuthenticateWithRetryAsync(CancellationToken ct)
    {
        const int maxRetries = 8;
        var delay = TimeSpan.FromSeconds(10);

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _logger.LogInformation("Authenticating with Capital.com (attempt {Attempt}/{Max})...", attempt, maxRetries);
                await _api.AuthenticateAsync(ct);
                _logger.LogInformation("Authentication successful");
                return;
            }
            catch (InvalidOperationException ex) when (
                (ex.Message.Contains("429") || ex.Message.Contains("500") || ex.Message.Contains("503"))
                && attempt < maxRetries)
            {
                _logger.LogWarning("Auth failed ({Message}). Retry {Attempt}/{Max} in {Delay}s",
                    ex.Message, attempt, maxRetries, delay.TotalSeconds);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 300));
            }
            catch (HttpRequestException ex) when (attempt < maxRetries)
            {
                _logger.LogWarning(ex, "Auth network error. Retry {Attempt}/{Max} in {Delay}s",
                    attempt, maxRetries, delay.TotalSeconds);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 300));
            }
        }
    }
}
