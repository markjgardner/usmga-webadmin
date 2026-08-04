using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Usmga.FunctionApp.Options;

namespace Usmga.FunctionApp.Services;

public sealed class TelegramClient : IMessageChannel
{
    private readonly HttpClient _httpClient;
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramClient> _logger;

    public TelegramClient(HttpClient httpClient, IOptions<TelegramOptions> options, ILogger<TelegramClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<long?> SendAsync(string chatId, string message, IReadOnlyList<MessageButton>? buttons, CancellationToken cancellationToken)
    {
        object payload = buttons is { Count: > 0 }
            ? new
            {
                chat_id = chatId,
                text = message,
                reply_markup = new { inline_keyboard = new[] { buttons.Select(b => new { text = b.Text, callback_data = b.Data }).ToArray() } }
            }
            : new { chat_id = chatId, text = message };

        var response = await _httpClient.PostAsJsonAsync(Endpoint("sendMessage"), payload, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await ReadMessageIdAsync(response, cancellationToken);
    }

    public async Task ClearButtonsAsync(string chatId, long messageId, CancellationToken cancellationToken)
    {
        // Best effort: the buttons are a convenience, and every action they trigger is
        // re-validated server side. Telegram also rejects a no-op edit with 400, which is
        // not worth surfacing to the user.
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                Endpoint("editMessageReplyMarkup"),
                new { chat_id = chatId, message_id = messageId },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Could not clear buttons on message {MessageId}: {Status}", messageId, response.StatusCode);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogInformation(ex, "Could not clear buttons on message {MessageId}", messageId);
        }
    }

    public async Task AcknowledgeAsync(string callbackQueryId, string? text, CancellationToken cancellationToken)
    {
        // Telegram shows a spinner on the tapped button until this is called.
        try
        {
            object payload = text is null
                ? new { callback_query_id = callbackQueryId }
                : new { callback_query_id = callbackQueryId, text };

            var response = await _httpClient.PostAsJsonAsync(Endpoint("answerCallbackQuery"), payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("answerCallbackQuery returned {Status}", response.StatusCode);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogInformation(ex, "answerCallbackQuery failed");
        }
    }

    private string Endpoint(string method) => $"https://api.telegram.org/bot{_options.BotToken}/{method}";

    private static async Task<long?> ReadMessageIdAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (document.RootElement.TryGetProperty("result", out var result)
                && result.TryGetProperty("message_id", out var id)
                && id.TryGetInt64(out var value))
            {
                return value;
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }
}
