using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Usmga.FunctionApp.Models;
using Usmga.FunctionApp.Options;
using Usmga.FunctionApp.Services;

namespace Usmga.FunctionApp.Functions;

public sealed class TelegramInbound
{
    private const string SecretHeaderName = "X-Telegram-Bot-Api-Secret-Token";
    private readonly MessageClassifier _classifier;
    private readonly IStateStore _state;
    private readonly IMessageChannel _channel;
    private readonly RequestProcessor _processor;
    private readonly TelegramOptions _telegramOptions;
    private readonly ILogger<TelegramInbound> _logger;

    public TelegramInbound(MessageClassifier classifier, IStateStore state, IMessageChannel channel, RequestProcessor processor, IOptions<TelegramOptions> telegramOptions, ILogger<TelegramInbound> logger)
    {
        _classifier = classifier;
        _state = state;
        _channel = channel;
        _processor = processor;
        _telegramOptions = telegramOptions.Value;
        _logger = logger;
    }

    [Function("TelegramInbound")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "telegram/webhook")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        if (!IsAuthorized(req))
        {
            _logger.LogWarning("Telegram webhook secret validation failed");
            return req.CreateResponse(HttpStatusCode.Forbidden);
        }

        var body = await req.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(body))
        {
            _logger.LogWarning("Empty Telegram request body");
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        TelegramUpdate? update;
        try
        {
            update = JsonSerializer.Deserialize<TelegramUpdate>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Malformed Telegram webhook payload");
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var message = update?.Message;
        var callback = update?.CallbackQuery;

        if (message is null && callback is null)
        {
            return req.CreateResponse(HttpStatusCode.OK);
        }

        var updateId = update!.UpdateId.ToString();

        if (callback is not null)
        {
            return await HandleCallbackAsync(req, callback, updateId, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(message!.Text) || message.From is null || message.Chat is null)
        {
            return req.CreateResponse(HttpStatusCode.OK);
        }

        var userId = message.From.Id.ToString();
        var chatId = message.Chat.Id.ToString();

        if (!await _state.TryClaimMessageAsync(updateId, cancellationToken))
        {
            _logger.LogInformation("Ignoring duplicate or in-flight Telegram update {UpdateId}", updateId);
            return req.CreateResponse(HttpStatusCode.OK);
        }

        try
        {
            if (!_classifier.IsAllowed(userId))
            {
                await _channel.SendAsync(chatId, "This USMGA website Telegram bot only accepts requests from authorized board members.", cancellationToken);
                await _state.CompleteMessageAsync(updateId, cancellationToken);
                return req.CreateResponse(HttpStatusCode.OK);
            }

            await _processor.HandleMessageAsync(chatId, message.Text, message.ReplyToMessage?.MessageId, cancellationToken);

            await _state.CompleteMessageAsync(updateId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telegram update {UpdateId} failed; releasing idempotency claim for retry", updateId);
            await _state.ReleaseMessageAsync(updateId, cancellationToken);
            throw;
        }

        return req.CreateResponse(HttpStatusCode.OK);
    }

    /// <summary>Handles an inline keyboard tap.</summary>
    /// <remarks>
    /// This is a second, independent path into the merge logic, so it must repeat every
    /// authorization check the message path performs — in particular the allowlist. A
    /// callback query carries its own <c>from</c>, which is not necessarily the chat owner.
    /// </remarks>
    private async Task<HttpResponseData> HandleCallbackAsync(HttpRequestData req, TelegramCallbackQuery callback, string updateId, CancellationToken cancellationToken)
    {
        var chat = callback.Message?.Chat;
        if (callback.From is null || chat is null)
        {
            return req.CreateResponse(HttpStatusCode.OK);
        }

        var userId = callback.From.Id.ToString();
        var chatId = chat.Id.ToString();

        if (!_classifier.IsAllowed(userId))
        {
            _logger.LogWarning("Rejected callback query from non-allowlisted user {UserId}", userId);
            await _channel.AcknowledgeAsync(callback.Id, "Not authorized.", cancellationToken);
            return req.CreateResponse(HttpStatusCode.OK);
        }

        if (!await _state.TryClaimMessageAsync(updateId, cancellationToken))
        {
            _logger.LogInformation("Ignoring duplicate or in-flight Telegram callback {UpdateId}", updateId);
            return req.CreateResponse(HttpStatusCode.OK);
        }

        try
        {
            await _processor.HandleCallbackAsync(chatId, callback.Id, callback.Data, callback.Message?.MessageId, cancellationToken);
            await _state.CompleteMessageAsync(updateId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telegram callback {UpdateId} failed; releasing idempotency claim for retry", updateId);
            await _state.ReleaseMessageAsync(updateId, cancellationToken);
            throw;
        }

        return req.CreateResponse(HttpStatusCode.OK);
    }

    private bool IsAuthorized(HttpRequestData request)
    {
        if (string.IsNullOrWhiteSpace(_telegramOptions.WebhookSecret)) return false;
        if (!request.Headers.TryGetValues(SecretHeaderName, out var values)) return false;

        var expected = Encoding.UTF8.GetBytes(_telegramOptions.WebhookSecret);
        return values.Any(value => FixedTimeEquals(value, expected));
    }

    private static bool FixedTimeEquals(string? value, byte[] expected)
    {
        if (value is null) return false;
        var actual = Encoding.UTF8.GetBytes(value);
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
