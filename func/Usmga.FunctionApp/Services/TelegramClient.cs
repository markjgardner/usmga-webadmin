using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Usmga.FunctionApp.Options;

namespace Usmga.FunctionApp.Services;

public sealed class TelegramClient : IMessageChannel
{
    private readonly HttpClient _httpClient;
    private readonly TelegramOptions _options;

    public TelegramClient(HttpClient httpClient, IOptions<TelegramOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public async Task SendAsync(string chatId, string message, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"https://api.telegram.org/bot{_options.BotToken}/sendMessage",
            new { chat_id = chatId, text = message },
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
