namespace Usmga.FunctionApp.Models;

public enum CallbackActionKind
{
    Approve,
    Changes
}

/// <summary>
/// Payload carried by an inline keyboard button.
/// </summary>
/// <remarks>
/// Telegram caps <c>callback_data</c> at 64 bytes. The encoding is
/// <c>{a|c}:{code}[:{nonce}]</c>, which for a 6-character code and a 16-character
/// nonce is about 25 bytes.
///
/// The nonce travels in the payload rather than being retyped, so a button tap is bound
/// to the request exactly as strongly as the legacy typed command. The payload is not a
/// bearer token on its own: the handler still verifies chat ownership, nonce validity,
/// reviewed SHA and check status before merging.
/// </remarks>
public sealed record CallbackAction(CallbackActionKind Kind, string Code, string? ApprovalNonce)
{
    public const int MaxDataLength = 64;

    public static string Format(CallbackActionKind kind, string code, string? nonce = null)
    {
        var prefix = kind switch
        {
            CallbackActionKind.Approve => "a",
            CallbackActionKind.Changes => "c",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        var data = nonce is null ? $"{prefix}:{code}" : $"{prefix}:{code}:{nonce}";
        if (data.Length > MaxDataLength)
        {
            throw new InvalidOperationException($"callback_data exceeds Telegram's {MaxDataLength}-byte limit: {data.Length}");
        }

        return data;
    }

    public static CallbackAction? Parse(string? data)
    {
        if (string.IsNullOrWhiteSpace(data) || data.Length > MaxDataLength)
        {
            return null;
        }

        var parts = data.Split(':');
        if (parts.Length is < 2 or > 3)
        {
            return null;
        }

        var kind = parts[0] switch
        {
            "a" => CallbackActionKind.Approve,
            "c" => CallbackActionKind.Changes,
            _ => (CallbackActionKind?)null
        };

        if (kind is null || string.IsNullOrWhiteSpace(parts[1]))
        {
            return null;
        }

        var nonce = parts.Length == 3 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2] : null;
        if (kind == CallbackActionKind.Approve && nonce is null)
        {
            return null;
        }

        return new CallbackAction(kind.Value, parts[1].ToUpperInvariant(), nonce);
    }
}
