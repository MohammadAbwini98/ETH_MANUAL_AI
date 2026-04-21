using EthSignal.Infrastructure.Trading;

namespace EthSignal.Web.BackgroundServices;

public sealed class TradeExecutionQueueWorkerService : BackgroundService
{
    private readonly ITradeExecutionQueueService _queueService;
    private readonly ILogger<TradeExecutionQueueWorkerService> _logger;

    public TradeExecutionQueueWorkerService(
        ITradeExecutionQueueService queueService,
        ILogger<TradeExecutionQueueWorkerService> logger)
    {
        _queueService = queueService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _queueService.NotifyWorkAvailable();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _queueService.WaitForWorkAsync(stoppingToken);
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
        }
    }
}
