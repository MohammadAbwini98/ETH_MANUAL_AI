using EthSignal.Infrastructure.Trading;

namespace EthSignal.Web.BackgroundServices;

public sealed class ExecutedTradeMonitorService : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly TradeLifecycleReconciliationService _reconciliationService;
    private readonly ILogger<ExecutedTradeMonitorService> _logger;
    private readonly TimeSpan _pollInterval;

    public ExecutedTradeMonitorService(
        IConfiguration config,
        TradeLifecycleReconciliationService reconciliationService,
        ILogger<ExecutedTradeMonitorService> logger)
    {
        _config = config;
        _reconciliationService = reconciliationService;
        _logger = logger;
        var pollSeconds = Math.Clamp(_config.GetValue("CapitalTrading:LifecyclePollIntervalSeconds", 5), 1, 60);
        _pollInterval = TimeSpan.FromSeconds(pollSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_config.GetValue("CapitalTrading:Enabled", false))
                    await _reconciliationService.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TradeLifecycle] Poll cycle failed");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }
    }
}
