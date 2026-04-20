using EthSignal.Infrastructure.Trading;

namespace EthSignal.Web.BackgroundServices;

public sealed class TradeExecutionQueueWorkerService : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly ITradeExecutionQueueService _queueService;
    private readonly ILogger<TradeExecutionQueueWorkerService> _logger;
    private readonly TimeSpan _pollInterval;

    public TradeExecutionQueueWorkerService(
        IConfiguration config,
        ITradeExecutionQueueService queueService,
        ILogger<TradeExecutionQueueWorkerService> logger)
    {
        _config = config;
        _queueService = queueService;
        _logger = logger;
        var pollSeconds = Math.Clamp(_config.GetValue("CapitalTrading:QueuePollIntervalSeconds", 1), 1, 60);
        _pollInterval = TimeSpan.FromSeconds(pollSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _queueService.DrainAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TradeExecutionQueueWorker] Queue drain failed");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }
    }
}
