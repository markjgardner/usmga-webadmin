using System.Text.Json.Serialization;

namespace Usmga.FunctionApp.Models;

public sealed record TelegramUpdate(
    [property: JsonPropertyName("update_id")] long UpdateId,
    [property: JsonPropertyName("message")] TelegramMessage? Message,
    [property: JsonPropertyName("callback_query")] TelegramCallbackQuery? CallbackQuery = null);

public sealed record TelegramMessage(
    [property: JsonPropertyName("message_id")] long MessageId,
    [property: JsonPropertyName("from")] TelegramUser? From,
    [property: JsonPropertyName("chat")] TelegramChat? Chat,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("reply_to_message")] TelegramMessage? ReplyToMessage = null);

/// <summary>An inline keyboard button tap.</summary>
/// <remarks>
/// Only delivered when the webhook is registered with <c>callback_query</c> in
/// <c>allowed_updates</c>; see scripts/register-telegram-webhook.sh. <see cref="Id"/> must
/// be passed to <c>answerCallbackQuery</c> or the client shows a spinner indefinitely.
/// </remarks>
public sealed record TelegramCallbackQuery(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("from")] TelegramUser? From,
    [property: JsonPropertyName("message")] TelegramMessage? Message,
    [property: JsonPropertyName("data")] string? Data);

public sealed record TelegramUser([property: JsonPropertyName("id")] long Id);

public sealed record TelegramChat([property: JsonPropertyName("id")] long Id);
