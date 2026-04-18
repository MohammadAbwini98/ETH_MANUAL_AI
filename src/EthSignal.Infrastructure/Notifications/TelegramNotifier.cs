using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace EthSignal.Infrastructure.Notifications;

public sealed class TelegramNotifier : ITelegramNotifier
{
    private readonly HttpClient _http;
    private readonly string _token;
    private readonly IReadOnlyList<long> _chatIds;
    private readonly ILogger<TelegramNotifier> _logger;

    public TelegramNotifier(
        string botToken,
        IEnumerable<long> chatIds,
        ILogger<TelegramNotifier> logger)
    {
        _token = botToken;
        _chatIds = chatIds.ToList().AsReadOnly();
        _logger = logger;
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://api.telegram.org/"),
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    public async Task SendAsync(string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_token) || _chatIds.Count == 0)
        {
            _logger.LogDebug("[Telegram] Skipped — bot token or chat IDs not configured");
            return;
        }

        foreach (var chatId in _chatIds)
        {
            try
            {
                var payload = new
                {
                    chat_id = chatId,
                    text = message,
                    parse_mode = "HTML"
                };

                var url = $"https://api.telegram.org/bot{_token}/sendMessage";
                var response = await _http.PostAsJsonAsync(url, payload, ct);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning(
                        "[Telegram] Message to chat {ChatId} failed ({Status}): {Body}",
                        chatId, (int)response.StatusCode, body);
                }
                else
                {
                    _logger.LogDebug("[Telegram] Message sent to chat {ChatId}", chatId);
                }
            }
            catch (Exception ex)
            {
                // Never propagate — notifications must not crash the engine
                _logger.LogWarning(ex, "[Telegram] Exception sending to chat {ChatId} (non-fatal)", chatId);
            }
        }
    }
}
