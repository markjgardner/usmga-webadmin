using System.Text.RegularExpressions;
using Usmga.FunctionApp.Models;

namespace Usmga.FunctionApp.Services;

/// <summary>
/// Works out what an inbound message means, given what is currently in flight for the chat.
/// </summary>
/// <remarks>
/// Deliberately an interface: the rule-based implementation costs nothing and is fully
/// deterministic, but an LLM-backed implementation can be substituted without touching
/// callers if the vocabulary ever outgrows phrase matching.
/// </remarks>
public interface IIntentClassifier
{
    IntentResult Classify(string message, ConversationContext context);
}

public sealed class RuleBasedIntentClassifier : IIntentClassifier
{
    // Legacy explicit syntax. Kept working forever: it is unambiguous, scriptable, and
    // already documented to users.
    private static readonly Regex ApprovePattern = new(@"^\s*APPROVE\s+(?<code>[A-Za-z0-9-]+)\s+(?<nonce>[A-Za-z0-9_\-]+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ChangesPattern = new(@"^\s*CHANGES\s+(?<code>[A-Za-z0-9-]+)\s*:\s*(?<text>.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// "approve ABC123" — names a request but not a nonce. Resolved from stored state
    /// rather than rejected, which is how a user picks between two pending previews by typing.
    /// </summary>
    /// <remarks>
    /// Restricted to the exact shape of a generated code (see <see cref="SecureTokenGenerator"/>),
    /// and only consulted after phrase matching has failed, so "approve this" stays an approval
    /// instead of being read as a request named THIS.
    /// </remarks>
    private static readonly Regex ApproveByCodePattern = new(@"^\s*APPROVE\s+(?<code>[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]{6})\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// An attempt at the legacy uppercase syntax that did not parse. Case-sensitive on
    /// purpose: the commands are only ever shown uppercase, so ordinary prose that happens
    /// to begin with "approve after you fix…" is still read as conversation.
    /// </summary>
    private static readonly Regex LegacyPrefixPattern = new(@"^\s*(APPROVE|CHANGES)\s+\S", RegexOptions.Compiled);

    private static readonly Regex SlashCommand = new(@"^\s*/(?<name>[a-z_]+)(?:@\S+)?\s*(?<rest>.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Phrases that mean "publish it" and nothing else.</summary>
    private static readonly string[] HighConfidenceApprovals =
    {
        "approve", "approved", "i approve", "approve it", "approve this",
        "lgtm", "looks good", "looks good to me", "looks great", "looks perfect",
        "ship it", "ship this", "publish it", "publish this", "publish",
        "go live", "make it live", "send it", "push it live", "merge it", "merge",
        "good to go", "go ahead", "go for it"
    };

    /// <summary>Phrases that probably mean approval but are too vague to merge on.</summary>
    private static readonly string[] MediumConfidenceApprovals =
    {
        "ok", "okay", "k", "yes", "yep", "yeah", "yup", "sure", "fine",
        "nice", "great", "perfect", "love it", "beautiful", "awesome", "cool",
        "thanks", "thank you", "ty", "done", "good", "do it",
        "nice work", "great work", "good work", "all good", "sure thing"
    };

    private static readonly string[] CancelPhrases =
    {
        "cancel", "cancel it", "abandon", "abandon it", "forget it", "never mind", "nevermind", "drop it", "scrap it"
    };

    private static readonly string[] StatusPhrases =
    {
        "status", "what's pending", "whats pending", "what is pending", "anything pending", "what's outstanding", "whats outstanding"
    };

    private static readonly string[] RejectionPhrases =
    {
        "no", "nope", "not yet", "don't", "dont", "stop", "wait", "hold on", "hold off"
    };

    public IntentResult Classify(string message, ConversationContext context)
    {
        var raw = message ?? string.Empty;

        var approve = ApprovePattern.Match(raw);
        if (approve.Success)
        {
            return new IntentResult(
                IntentKind.Approve,
                IntentConfidence.High,
                approve.Groups["code"].Value.ToUpperInvariant(),
                approve.Groups["nonce"].Value,
                string.Empty);
        }

        var changes = ChangesPattern.Match(raw);
        if (changes.Success)
        {
            return new IntentResult(
                IntentKind.Changes,
                IntentConfidence.High,
                changes.Groups["code"].Value.ToUpperInvariant(),
                null,
                changes.Groups["text"].Value.Trim());
        }

        var slash = SlashCommand.Match(raw);
        if (slash.Success)
        {
            return ClassifySlashCommand(slash.Groups["name"].Value.ToLowerInvariant(), slash.Groups["rest"].Value.Trim(), context);
        }

        var approveByCode = ApproveByCodePattern.Match(raw);

        var normalized = Normalize(raw);
        if (normalized.Length == 0)
        {
            return IntentResult.Of(IntentKind.Unknown, IntentConfidence.Low);
        }

        // Only a code if it is not simply an approval phrase. "approve thanks" and
        // "approve please" happen to fit the code shape; they are approvals, not codes.
        if (approveByCode.Success
            && !MatchesAny(normalized, HighConfidenceApprovals)
            && !MatchesAny(normalized, MediumConfidenceApprovals))
        {
            return new IntentResult(IntentKind.Approve, IntentConfidence.High, approveByCode.Groups["code"].Value.ToUpperInvariant(), null, string.Empty);
        }

        // A question is not an instruction. "publish?" and "go live?" ask whether it is time,
        // and punctuation is the only thing distinguishing them from the imperative, so they
        // are demoted to the confirmation path rather than merged outright.
        var approvalConfidence = IsQuestion(raw) ? IntentConfidence.Medium : IntentConfidence.High;

        // A pending confirmation narrows the vocabulary: the only question on the table is
        // yes or no, so bare affirmations become decisive rather than ambiguous.
        if (context.Approvable.Any(r => r.IsAwaitingConfirmation()))
        {
            // "no" here answers "shall I publish?" — it does not mean "abandon the whole
            // request", which would be destructive and is what /cancel is for.
            if (MatchesAny(normalized, RejectionPhrases))
            {
                return IntentResult.Of(IntentKind.Decline, IntentConfidence.High);
            }

            if (MatchesAny(normalized, MediumConfidenceApprovals) || MatchesAny(normalized, HighConfidenceApprovals))
            {
                return IntentResult.Of(IntentKind.Approve, approvalConfidence);
            }
        }

        if (MatchesAny(normalized, StatusPhrases))
        {
            return IntentResult.Of(IntentKind.Status, IntentConfidence.High);
        }

        if (MatchesAny(normalized, CancelPhrases))
        {
            return IntentResult.Of(IntentKind.Cancel, IntentConfidence.High);
        }

        // Approval phrases are only meaningful when something is actually awaiting one.
        // Without this guard "looks good" with nothing pending would be read as an
        // approval instead of the (odd, but harmless) new request that it is.
        if (context.HasPendingPreview)
        {
            // "not yet" / "hold on" declines this preview without abandoning it.
            if (MatchesAny(normalized, RejectionPhrases))
            {
                return IntentResult.Of(IntentKind.Decline, IntentConfidence.High);
            }

            if (MatchesAny(normalized, HighConfidenceApprovals))
            {
                return IntentResult.Of(IntentKind.Approve, approvalConfidence);
            }

            if (MatchesAny(normalized, MediumConfidenceApprovals))
            {
                return IntentResult.Of(IntentKind.Approve, IntentConfidence.Medium);
            }

            // Anything else, while a preview is waiting, is a revision to it. This is the
            // conversational reading: you are talking about the thing in front of you.
            // /new is the documented escape hatch and is named in every reply.
            return LooksLikeMalformedLegacyCommand(raw)
                ? IntentResult.Of(IntentKind.Help, IntentConfidence.High)
                : IntentResult.Of(IntentKind.Changes, IntentConfidence.High, raw.Trim());
        }

        // Nothing is publishable, so a bare reaction is not an instruction either. Turning
        // "looks good" or "thanks" into a new request would open a GitHub issue with that
        // as its entire body, so answer conversationally instead.
        if (MatchesAny(normalized, HighConfidenceApprovals))
        {
            return IntentResult.Of(IntentKind.Approve, IntentConfidence.High);
        }

        if (MatchesAny(normalized, MediumConfidenceApprovals) || MatchesAny(normalized, RejectionPhrases))
        {
            return IntentResult.Of(IntentKind.Status, IntentConfidence.High);
        }

        return LooksLikeMalformedLegacyCommand(raw)
            ? IntentResult.Of(IntentKind.Help, IntentConfidence.High)
            : IntentResult.Of(IntentKind.NewRequest, IntentConfidence.High, raw.Trim());
    }

    /// <summary>
    /// A half-typed legacy command, checked only after every phrase reading has failed so
    /// that plain words like "approve it" are unaffected.
    /// </summary>
    /// <remarks>
    /// Without this, "APPROVE abc123" (nonce omitted) would be forwarded to Copilot as a
    /// revision, or opened as a new issue with that as its entire body.
    /// </remarks>
    private static bool LooksLikeMalformedLegacyCommand(string raw) => LegacyPrefixPattern.IsMatch(raw);

    private static IntentResult ClassifySlashCommand(string name, string rest, ConversationContext context) => name switch
    {
        "start" or "help" => IntentResult.Of(IntentKind.Help, IntentConfidence.High),
        "status" => IntentResult.Of(IntentKind.Status, IntentConfidence.High),
        "cancel" => IntentResult.Of(IntentKind.Cancel, IntentConfidence.High),
        "new" => rest.Length > 0
            ? IntentResult.Of(IntentKind.NewRequest, IntentConfidence.High, rest)
            : IntentResult.Of(IntentKind.Help, IntentConfidence.High),
        "approve" => context.HasPendingPreview
            ? IntentResult.Of(IntentKind.Approve, IntentConfidence.High)
            : IntentResult.Of(IntentKind.Status, IntentConfidence.High),
        "changes" => rest.Length > 0
            ? IntentResult.Of(IntentKind.Changes, IntentConfidence.High, rest)
            : IntentResult.Of(IntentKind.Help, IntentConfidence.High),
        _ => IntentResult.Of(IntentKind.Help, IntentConfidence.High)
    };

    /// <summary>
    /// True when the message is phrased as a question, which <see cref="Normalize"/> would
    /// otherwise erase along with the rest of the punctuation.
    /// </summary>
    private static bool IsQuestion(string message) => message.TrimEnd().EndsWith('?');

    /// <summary>
    /// Lowercases, strips punctuation and emoji, and collapses whitespace so that
    /// "Looks good!!" and "looks good" compare equal.
    /// </summary>
    private static string Normalize(string message)
    {
        var chars = message.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c == '\'' || char.IsWhiteSpace(c) ? c : ' ');
        return string.Join(' ', new string(chars.ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>
    /// True only when the message is *essentially just* one of the phrases.
    /// </summary>
    /// <remarks>
    /// This is the precision guard, and the most important rule in the class. Substring
    /// matching would read "looks good but can you make the logo bigger" as an approval
    /// and publish the site instead of requesting the change. Instead the message must
    /// reduce to exactly one of the phrases once conversational filler is removed, at the
    /// cost of occasionally routing a wordier approval to the (safe, reversible) changes
    /// path instead.
    /// </remarks>
    private static bool MatchesAny(string normalized, string[] phrases)
    {
        if (phrases.Contains(normalized))
        {
            return true;
        }

        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // A long message is a substantive instruction, never a bare approval, however
        // many filler words it happens to contain.
        if (words.Length > MaxMeaningfulWords)
        {
            return false;
        }

        var stripped = string.Join(' ', words.Where(w => !Filler.Contains(w)));
        if (stripped.Length > 0 && phrases.Contains(stripped))
        {
            return true;
        }

        return IsPhraseCover(words, phrases);
    }

    /// <summary>
    /// True when the message is nothing but approval phrases and filler, in any
    /// combination — "looks good, I approve", "ok thanks", "do it now".
    /// </summary>
    /// <remarks>
    /// Consumes the message left to right, taking the longest phrase available at each
    /// position and skipping filler. Any word that is neither fails the whole match, which
    /// is what keeps "looks good but make the logo bigger" out of the approval path.
    /// A phrase is preferred over filler at the same position so that words which are both
    /// ("ok", "thanks") still count.
    /// </remarks>
    private static bool IsPhraseCover(string[] words, string[] phrases)
    {
        var maxPhraseWords = phrases.Max(p => p.Count(c => c == ' ') + 1);
        var matchedAny = false;

        for (var i = 0; i < words.Length;)
        {
            var matchedLength = 0;
            for (var length = Math.Min(maxPhraseWords, words.Length - i); length >= 1; length--)
            {
                if (phrases.Contains(string.Join(' ', words.Skip(i).Take(length))))
                {
                    matchedLength = length;
                    break;
                }
            }

            if (matchedLength > 0)
            {
                matchedAny = true;
                i += matchedLength;
                continue;
            }

            if (Filler.Contains(words[i]))
            {
                i++;
                continue;
            }

            return false;
        }

        return matchedAny;
    }

    /// <summary>
    /// Above this word count a message is treated as an instruction rather than a
    /// reaction, regardless of its wording.
    /// </summary>
    private const int MaxMeaningfulWords = 8;

    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "i", "it", "this", "that", "please", "pls", "plz", "thanks", "thank", "ty",
        "and", "then", "just", "all", "well", "very", "really", "so", "now", "lets", "let's",
        "we", "my", "the", "a", "is", "its", "it's", "im", "i'm", "ok", "okay",
        "to", "too", "for", "yeah", "yep", "still", "actually", "definitely", "absolutely", "much"
    };
}
