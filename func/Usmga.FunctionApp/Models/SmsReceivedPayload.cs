using System.Text.Json.Serialization;

namespace Usmga.FunctionApp.Models;

public sealed class SmsReceivedPayload
{
    [JsonPropertyName("messageId")]
    public string MessageId { get; set; } = string.Empty;
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;
    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
    [JsonPropertyName("receivedTimestamp")]
    public DateTimeOffset ReceivedTimestamp { get; set; }
    [JsonPropertyName("segmentCount")]
    public int SegmentCount { get; set; }
}
