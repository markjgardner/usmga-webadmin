namespace Usmga.FunctionApp.Models;

public enum IntentKind
{
    /// <summary>No actionable intent could be determined.</summary>
    Unknown,

    /// <summary>Publish the pending preview.</summary>
    Approve,

    /// <summary>Revise the pending request.</summary>
    Changes,

    /// <summary>Start a brand new request.</summary>
    NewRequest,

    /// <summary>Report what is currently in flight.</summary>
    Status,

    /// <summary>Abandon the pending request.</summary>
    Cancel,

    /// <summary>Hold off publishing without abandoning anything.</summary>
    Decline,

    /// <summary>Explain how to talk to the bot.</summary>
    Help
}

public enum IntentConfidence
{
    /// <summary>Act only after the user confirms.</summary>
    Low,

    /// <summary>Ambiguous phrasing; ask for confirmation before anything irreversible.</summary>
    Medium,

    /// <summary>Unambiguous; act immediately.</summary>
    High
}

/// <param name="Kind">What the user wants to do.</param>
/// <param name="Confidence">How certain the classifier is. Approvals below <see cref="IntentConfidence.High"/> must be confirmed before merging.</param>
/// <param name="Code">Request code when the user named one explicitly; null means "resolve it from context".</param>
/// <param name="ApprovalNonce">Nonce when the user supplied one explicitly (legacy syntax or a button payload).</param>
/// <param name="Text">Payload text, e.g. the revision wording for <see cref="IntentKind.Changes"/>.</param>
public sealed record IntentResult(
    IntentKind Kind,
    IntentConfidence Confidence,
    string? Code,
    string? ApprovalNonce,
    string Text)
{
    public static IntentResult Of(IntentKind kind, IntentConfidence confidence, string text = "") =>
        new(kind, confidence, null, null, text);
}

/// <summary>
/// What the bot knows about the conversation when classifying a message.
/// </summary>
/// <remarks>
/// The Telegram Bot API does not let a bot read earlier messages, so "conversation
/// history" is reconstructed from persisted request state plus the message the user
/// replied to. That is exact rather than inferred.
/// </remarks>
/// <param name="ActiveRequests">Non-terminal requests for this chat, most recently updated first.</param>
/// <param name="RepliedToRequest">Request whose preview message the user replied to, when applicable.</param>
public sealed record ConversationContext(
    IReadOnlyList<RequestRecord> ActiveRequests,
    RequestRecord? RepliedToRequest)
{
    public static readonly ConversationContext Empty = new(Array.Empty<RequestRecord>(), null);

    /// <summary>Requests that could be published right now.</summary>
    public IReadOnlyList<RequestRecord> Approvable { get; } =
        ActiveRequests.Where(r => r.IsApprovable()).ToArray();

    /// <summary>True when the user has a preview waiting on them.</summary>
    public bool HasPendingPreview => Approvable.Count > 0;

    /// <summary>
    /// The request a bare message refers to. Prefers an explicit reply, then the only
    /// approvable request, then the most recently touched request.
    /// </summary>
    public RequestRecord? Implied =>
        RepliedToRequest
        ?? (Approvable.Count == 1 ? Approvable[0] : null)
        ?? (ActiveRequests.Count == 1 ? ActiveRequests[0] : null);
}
