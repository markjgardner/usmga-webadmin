namespace Usmga.FunctionApp.Models;

public sealed class RequestRecord
{
    public string Code { get; set; } = string.Empty;
    public string CorrelationNonce { get; set; } = string.Empty;
    public string RequesterChatId { get; set; } = string.Empty;
    public string OriginalMessage { get; set; } = string.Empty;
    public string Status { get; set; } = RequestStatus.New;
    public int? IssueNumber { get; set; }
    public int? PrNumber { get; set; }
    public string? PreviewUrl { get; set; }
    public string? ReviewedSha { get; set; }
    public string? DeployedSha { get; set; }
    public string? ApprovalNonce { get; set; }
    public DateTimeOffset? ApprovalNonceExpiresAt { get; set; }

    /// <summary>
    /// Telegram message id of the preview, so a reply to it binds back to this request
    /// and so its inline keyboard can be cleared once the request is resolved.
    /// </summary>
    public long? PreviewMessageId { get; set; }

    /// <summary>
    /// Set when the bot has asked "publish this?" after an ambiguous approval phrase.
    /// While unexpired, a bare "yes" confirms instead of re-prompting.
    /// </summary>
    public DateTimeOffset? AwaitingConfirmationUntil { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? LastError { get; set; }

    /// <summary>
    /// True when this request could be published right now: a preview was delivered for a
    /// known commit and the approval nonce it carried has not expired.
    /// </summary>
    /// <remarks>
    /// Requiring a live nonce is what stops a natural-language approval from acting on a
    /// request whose preview was never delivered or has gone stale. The user no longer has
    /// to type the nonce, but it must still exist.
    /// </remarks>
    public bool IsApprovable() =>
        Status == RequestStatus.PreviewDeployed
        && PrNumber is not null
        && !string.IsNullOrWhiteSpace(ReviewedSha)
        && !string.IsNullOrWhiteSpace(ApprovalNonce)
        && ApprovalNonceExpiresAt is not null
        && ApprovalNonceExpiresAt > DateTimeOffset.UtcNow;

    public bool IsAwaitingConfirmation() =>
        AwaitingConfirmationUntil is not null && AwaitingConfirmationUntil > DateTimeOffset.UtcNow;
}
