namespace EthSignal.Infrastructure.Notifications;

public interface ITelegramNotifier
{
    Task SendAsync(string message, CancellationToken ct = default);
}
