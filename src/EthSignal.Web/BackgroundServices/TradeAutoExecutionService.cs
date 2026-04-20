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
    private readonly ITradeExecutionService _executionService;
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
        ITradeExecutionService executionService,
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
        _executionService = executionService;
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
                    .Where(s => s.Direction is SignalDirection.BUY or SignalDirection.SELL)
                    .Select(_mapper.FromRecommended));
            }
        }

        if (settings.AllowedSourceTypes.Contains(SignalExecutionSourceType.Generated))
        {
            var recent = await _generatedHistory.GetHistoryAsync(symbol, 100, 0, null, ct);
            candidates.AddRange(recent.Signals.Select(s => _mapper.FromGenerated(s.Signal)));
        }

        if (settings.AllowedSourceTypes.Contains(SignalExecutionSourceType.Blocked))
        {
            var recent = await _blockedHistory.GetHistoryAsync(symbol, 100, 0, ct);
            candidates.AddRange(recent.Signals.Select(s => _mapper.FromBlocked(s.Signal)));
        }

        foreach (var candidate in candidates
                     .OrderByDescending(c => c.SignalTimeUtc))
        {
            if (DateTimeOffset.UtcNow - candidate.SignalTimeUtc > TimeSpan.FromMinutes(settings.StaleWindowMinutes))
                continue;

            var existing = await _executedTradeRepository.GetBySourceSignalAsync(candidate.SignalId, candidate.SourceType, ct);
            if (existing != null)
                continue;

            var result = await _executionService.ExecuteAsync(new TradeExecutionRequest
            {
                Candidate = candidate,
                RequestedBy = "auto-executor"
            }, ct);

            _logger.LogInformation(
                "[TradeAutoExecution] Source={Source} SignalId={SignalId} Status={Status} Success={Success}",
                candidate.SourceType, candidate.SignalId, result.Status, result.Success);
        }
    }
}
