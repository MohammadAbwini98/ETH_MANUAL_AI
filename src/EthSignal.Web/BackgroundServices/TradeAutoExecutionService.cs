using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Db;
using EthSignal.Infrastructure.Engine;
using EthSignal.Infrastructure.Trading;

namespace EthSignal.Web.BackgroundServices;

public sealed class TradeAutoExecutionService : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly ISignalRepository _signalRepository;
    private readonly IBlockedSignalHistoryService _blockedHistory;
    private readonly IGeneratedSignalHistoryService _generatedHistory;
    private readonly IExecutionCandidateMapper _mapper;
    private readonly ITradeExecutionPolicy _policy;
    private readonly ITradeExecutionQueueService _queueService;
    private readonly IExecutedTradeRepository _executedTradeRepository;
    private readonly IPortalOverridesRepository _portalOverridesRepository;
    private readonly ILogger<TradeAutoExecutionService> _logger;
    private readonly TimeSpan _pollInterval;
    private bool? _lastRecommendedExecutionEnabled;

    public TradeAutoExecutionService(
        IConfiguration config,
        ISignalRepository signalRepository,
        IBlockedSignalHistoryService blockedHistory,
        IGeneratedSignalHistoryService generatedHistory,
        IExecutionCandidateMapper mapper,
        ITradeExecutionPolicy policy,
        ITradeExecutionQueueService queueService,
        IExecutedTradeRepository executedTradeRepository,
        IPortalOverridesRepository portalOverridesRepository,
        ILogger<TradeAutoExecutionService> logger)
    {
        _config = config;
        _signalRepository = signalRepository;
        _blockedHistory = blockedHistory;
        _generatedHistory = generatedHistory;
        _mapper = mapper;
        _policy = policy;
        _queueService = queueService;
        _executedTradeRepository = executedTradeRepository;
        _portalOverridesRepository = portalOverridesRepository;
        _logger = logger;
        var pollSeconds = Math.Clamp(_config.GetValue("CapitalTrading:AutoExecutePollIntervalSeconds", 1), 1, 60);
        _pollInterval = TimeSpan.FromSeconds(pollSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TradeAutoExecution] Poll cycle failed");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var settings = _policy.GetSettings();
        if (!(settings.Enabled && settings.AutoExecuteEnabled))
            return;

        var overrides = await _portalOverridesRepository.GetAsync(ct);
        var recommendedExecutionEnabled = overrides?.RecommendedSignalExecutionEnabled ?? true;
        if (_lastRecommendedExecutionEnabled != recommendedExecutionEnabled)
        {
            _lastRecommendedExecutionEnabled = recommendedExecutionEnabled;
            _logger.LogInformation(
                "[TradeAutoExecution] Recommended auto execution {State} (portal override {Mode})",
                recommendedExecutionEnabled ? "ENABLED" : "DISABLED",
                overrides?.RecommendedSignalExecutionEnabled.HasValue == true ? "set" : "default");
        }

        await AutoExecuteAsync(settings, recommendedExecutionEnabled, ct);
    }

    private async Task AutoExecuteAsync(TradeExecutionPolicySettings settings, bool recommendedExecutionEnabled, CancellationToken ct)
    {
        var symbol = _config["CapitalApi:Symbol"] ?? "ETHUSD";
        var candidates = new List<TradeExecutionCandidate>();

        if (settings.AllowedSourceTypes.Contains(SignalExecutionSourceType.Recommended))
        {
            if (!recommendedExecutionEnabled)
            {
                var recentRecommended = await _signalRepository.GetSignalHistoryAsync(symbol, 25, ct)
                    ?? Array.Empty<SignalRecommendation>();
                var skippedCount = recentRecommended.Count(s => s.Direction is SignalDirection.BUY or SignalDirection.SELL);
                if (skippedCount > 0)
                {
                    _logger.LogInformation(
                        "[TradeAutoExecution] Skipping {Count} recommended auto-execution candidates because RecommendedSignalExecutionEnabled is OFF",
                        skippedCount);
                }
            }
            else
            {
                var recent = await _signalRepository.GetSignalHistoryAsync(symbol, 100, ct)
                    ?? Array.Empty<SignalRecommendation>();
                candidates.AddRange(recent
                    .Where(s => s.Status == SignalStatus.OPEN)
                    .Where(s => s.Direction is SignalDirection.BUY or SignalDirection.SELL)
                    .Select(_mapper.FromRecommended));
            }
        }

        if (settings.AllowedSourceTypes.Contains(SignalExecutionSourceType.Generated))
        {
            var recent = await _generatedHistory.GetHistoryAsync(symbol, 100, 0, null, ct);
            candidates.AddRange(recent.Signals
                .Where(IsActionableGeneratedSignal)
                .Select(s => _mapper.FromGenerated(s.Signal)));
        }

        if (settings.AllowedSourceTypes.Contains(SignalExecutionSourceType.Blocked))
        {
            var recent = await _blockedHistory.GetHistoryAsync(symbol, 100, 0, ct);
            candidates.AddRange(recent.Signals
                .Where(IsActionableBlockedSignal)
                .Select(s => _mapper.FromBlocked(s.Signal)));
        }

        if (candidates.Count == 0)
            return;

        var queueSnapshot = await _queueService.GetSnapshotAsync(1, ct);
        var queueInsertBudget = Math.Max(0, queueSnapshot.AvailableDispatchSlots - queueSnapshot.QueuedCount);
        if (queueInsertBudget <= 0)
        {
            _logger.LogInformation(
                "[TradeAutoExecution] Skipping auto-queueing because execution queue has no dispatch budget (AvailableSlots={AvailableSlots}, Queued={QueuedCount}, BrokerOpen={BrokerOpenTradeCount}, MaxOpenTrades={MaxConcurrentOpenTrades})",
                queueSnapshot.AvailableDispatchSlots,
                queueSnapshot.QueuedCount,
                queueSnapshot.BrokerOpenTradeCount,
                queueSnapshot.MaxConcurrentOpenTrades);
            return;
        }

        foreach (var candidate in candidates
                     .OrderBy(c => c.SignalTimeUtc))
        {
            if (queueInsertBudget <= 0)
                break;

            if (!TradeExecutionPolicy.IsBrokerExecutionTimeframeAllowed(candidate.Timeframe))
            {
                _logger.LogInformation(
                    "[TradeAutoExecution] Skipping {SourceType} signal {SignalId} because timeframe {Timeframe} is not broker-executable",
                    candidate.SourceType,
                    candidate.SignalId,
                    candidate.Timeframe);
                continue;
            }

            if (DateTimeOffset.UtcNow - candidate.SignalTimeUtc > TimeSpan.FromMinutes(settings.StaleWindowMinutes))
            {
                _logger.LogInformation(
                    "[TradeAutoExecution] Skipping {SourceType} signal {SignalId} because it is stale ({AgeMinutes:F1}m > {StaleWindowMinutes}m)",
                    candidate.SourceType,
                    candidate.SignalId,
                    (DateTimeOffset.UtcNow - candidate.SignalTimeUtc).TotalMinutes,
                    settings.StaleWindowMinutes);
                continue;
            }

            var existing = await _executedTradeRepository.GetBySourceSignalAsync(candidate.SignalId, candidate.SourceType, ct);
            if (existing != null)
            {
                _logger.LogInformation(
                    "[TradeAutoExecution] Skipping {SourceType} signal {SignalId} because execution record {TradeId} already exists with status {Status}",
                    candidate.SourceType,
                    candidate.SignalId,
                    existing.ExecutedTradeId,
                    existing.Status);
                continue;
            }

            var result = await _queueService.EnqueueAsync(new TradeExecutionRequest
            {
                Candidate = candidate,
                RequestedBy = "auto-executor"
            }, ct);

            if (result.Accepted && result.CreatedNewEntry)
            {
                queueInsertBudget--;
                _logger.LogInformation(
                    "[TradeAutoExecution] {SourceType} signal {SignalId} entered execution queue as entry {QueueEntryId}",
                    candidate.SourceType,
                    candidate.SignalId,
                    result.QueueEntryId);
            }
            else if (result.Accepted)
            {
                _logger.LogInformation(
                    "[TradeAutoExecution] {SourceType} signal {SignalId} is already queued as entry {QueueEntryId}",
                    candidate.SourceType,
                    candidate.SignalId,
                    result.QueueEntryId);
            }

            _logger.LogInformation(
                "[TradeAutoExecution] Source={Source} SignalId={SignalId} Status={Status} Success={Success}",
                candidate.SourceType, candidate.SignalId, result.Status, result.Accepted);
        }
    }

    private static bool IsActionableGeneratedSignal(GeneratedSignalWithOutcome item)
        => item.Signal.Direction is SignalDirection.BUY or SignalDirection.SELL
           && item.Signal.ExpiryTimeUtc > DateTimeOffset.UtcNow
           && item.Outcome.OutcomeLabel == OutcomeLabel.PENDING;

    private static bool IsActionableBlockedSignal(BlockedSignalWithOutcome item)
        => item.Signal.Direction is SignalDirection.BUY or SignalDirection.SELL
           && item.Signal.ExpiryTimeUtc > DateTimeOffset.UtcNow
           && item.Outcome.OutcomeLabel == OutcomeLabel.PENDING;
}
