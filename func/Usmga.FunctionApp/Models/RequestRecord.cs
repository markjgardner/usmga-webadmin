namespace Usmga.FunctionApp.Models;

public sealed class RequestRecord
{
    public string Code { get; set; } = string.Empty;
    public string CorrelationNonce { get; set; } = string.Empty;
    public string RequesterPhone { get; set; } = string.Empty;
    public string OriginalMessage { get; set; } = string.Empty;
    public string Status { get; set; } = RequestStatus.New;
    public int? IssueNumber { get; set; }
    public int? PrNumber { get; set; }
    public string? PreviewUrl { get; set; }
    public string? ReviewedSha { get; set; }
    public string? DeployedSha { get; set; }
    public string? ApprovalNonce { get; set; }
    public DateTimeOffset? ApprovalNonceExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? LastError { get; set; }
}
