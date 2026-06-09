namespace Usmga.FunctionApp.Models;

/// <summary>
/// Represents the relevant fields from a Twilio inbound SMS webhook POST.
/// Twilio sends form-encoded data; this model is populated after parsing.
/// </summary>
public sealed class SmsReceivedPayload
{
    public string MessageSid { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
}
