using Microsoft.Extensions.Options;
using Usmga.FunctionApp.Options;

namespace Usmga.FunctionApp.Services;

/// <summary>
/// Chat-level policy that is independent of what a message means: who may talk to the
/// bot, and whether a request implies the user has a file to upload.
/// </summary>
/// <remarks>
/// Parsing a message into an intent belongs to <see cref="IIntentClassifier"/>, which
/// needs conversation state that this type deliberately does not have.
/// </remarks>
public sealed class MessageClassifier
{
    private readonly HashSet<string> _allowed;

    public MessageClassifier(IOptions<TelegramOptions> options)
    {
        _allowed = options.Value.Allowlist.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
    }

    public bool IsAllowed(string userId) => _allowed.Count > 0 && _allowed.Contains(userId);

    public bool SuggestsAttachment(string message)
    {
        var lower = (message ?? string.Empty).ToLowerInvariant();
        return lower.Contains("attach") || lower.Contains("attachment") || lower.Contains("screenshot") || lower.Contains("photo") || lower.Contains("image") || lower.Contains("file");
    }
}
