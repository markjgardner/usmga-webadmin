using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Usmga.FunctionApp.Models;
using Usmga.FunctionApp.Options;

namespace Usmga.FunctionApp.Services;

public sealed class MessageClassifier
{
    private static readonly Regex ApprovePattern = new(@"^\s*APPROVE\s+(?<code>[A-Za-z0-9-]+)\s+(?<nonce>[A-Za-z0-9_\-]+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ApprovePrefixPattern = new(@"^\s*APPROVE\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ChangesPattern = new(@"^\s*CHANGES\s+(?<code>[A-Za-z0-9-]+)\s*:\s*(?<text>.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex ChangesPrefixPattern = new(@"^\s*CHANGES\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly HashSet<string> _allowed;

    public MessageClassifier(IOptions<TelegramOptions> options)
    {
        _allowed = options.Value.Allowlist.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
    }

    public bool IsAllowed(string userId) => _allowed.Count > 0 && _allowed.Contains(userId);

    public InboundCommand Classify(string message)
    {
        var approve = ApprovePattern.Match(message ?? string.Empty);
        if (approve.Success)
        {
            return new InboundCommand(InboundCommandKind.Approve, approve.Groups["code"].Value.ToUpperInvariant(), approve.Groups["nonce"].Value, string.Empty);
        }

        if (ApprovePrefixPattern.IsMatch(message ?? string.Empty))
        {
            return new InboundCommand(InboundCommandKind.Invalid, null, null, "Reply APPROVE <code> <approval-nonce> exactly as shown in the preview text.");
        }

        var changes = ChangesPattern.Match(message ?? string.Empty);
        if (changes.Success)
        {
            return new InboundCommand(InboundCommandKind.Changes, changes.Groups["code"].Value.ToUpperInvariant(), null, changes.Groups["text"].Value.Trim());
        }

        if (ChangesPrefixPattern.IsMatch(message ?? string.Empty))
        {
            return new InboundCommand(InboundCommandKind.Invalid, null, null, "Reply CHANGES <code>: <requested revision>.");
        }

        return new InboundCommand(InboundCommandKind.NewRequest, null, null, (message ?? string.Empty).Trim());
    }

    public bool SuggestsAttachment(string message)
    {
        var lower = (message ?? string.Empty).ToLowerInvariant();
        return lower.Contains("attach") || lower.Contains("attachment") || lower.Contains("screenshot") || lower.Contains("photo") || lower.Contains("image") || lower.Contains("file");
    }
}
