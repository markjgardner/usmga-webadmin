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
        if (message is null || string.IsNullOrWhiteSpace(message.Text) || message.From is null || message.Chat is null)
        {
            return req.CreateResponse(HttpStatusCode.OK);
        }

        var updateId = update!.UpdateId.ToString();
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

            var command = _classifier.Classify(message.Text);
            switch (command.Kind)
            {
                case InboundCommandKind.Approve:
                    await _processor.HandleApproveAsync(chatId, command.Code!, command.ApprovalNonce!, cancellationToken);
                    break;
                case InboundCommandKind.Changes:
                    await _processor.HandleChangesAsync(chatId, command.Code!, command.Text, cancellationToken);
                    break;
                case InboundCommandKind.Invalid:
                    await _channel.SendAsync(chatId, command.Text, cancellationToken);
                    break;
                default:
                    await _processor.HandleNewRequestAsync(chatId, command.Text, cancellationToken);
                    break;
            }

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
