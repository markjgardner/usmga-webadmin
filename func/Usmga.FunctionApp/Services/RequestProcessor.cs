using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Usmga.FunctionApp.Models;
using Usmga.FunctionApp.Options;

namespace Usmga.FunctionApp.Services;

public sealed class RequestProcessor
{
    private readonly IGitHubClient _gitHub;
    private readonly ISmsClient _sms;
    private readonly IStateStore _state;
    private readonly ITokenGenerator _tokens;
    private readonly MessageClassifier _classifier;
    private readonly SmsOptions _smsOptions;
    private readonly ILogger<RequestProcessor> _logger;

    public RequestProcessor(IGitHubClient gitHub, ISmsClient sms, IStateStore state, ITokenGenerator tokens, MessageClassifier classifier, IOptions<SmsOptions> smsOptions, ILogger<RequestProcessor> logger)
    {
        _gitHub = gitHub;
        _sms = sms;
        _state = state;
        _tokens = tokens;
        _classifier = classifier;
        _smsOptions = smsOptions.Value;
        _logger = logger;
    }

    public async Task HandleNewRequestAsync(string from, string text, CancellationToken cancellationToken)
    {
        var code = _tokens.NewRequestCode();
        var nonce = _tokens.NewNonce();
        var uploadLink = await MaybeCreateUploadLinkAsync(code, from, text, cancellationToken);
        var record = new RequestRecord
        {
            Code = code,
            CorrelationNonce = nonce,
            RequesterPhone = from,
            OriginalMessage = text,
            Status = RequestStatus.New
        };

        await _state.CreateRequestAsync(record, cancellationToken);
        try
        {
            await _gitHub.EnsureCopilotAssignableAsync(cancellationToken);
            var issue = await _gitHub.CreateIssueForCopilotAsync(BuildIssueTitle(code, nonce), BuildIssueBody(record, uploadLink), cancellationToken);
            record.IssueNumber = issue.Number;
            if (!issue.CopilotAssigned)
            {
                _logger.LogError("Created issue {IssueNumber} for request {Code}, but Copilot was not assigned", issue.Number, code);
                record.Status = RequestStatus.Failed;
                record.LastError = "Copilot was not assigned to the created issue.";
                record.UpdatedAt = DateTimeOffset.UtcNow;
                await _state.SaveRequestAsync(record, cancellationToken);
                await _sms.SendAsync(from, $"USMGA request {code} could not be started safely because Copilot was not assigned. Please contact the web admin.", cancellationToken);
                return;
            }

            record.Status = RequestStatus.AgentStarted;
            record.UpdatedAt = DateTimeOffset.UtcNow;
            await _state.SaveRequestAsync(record, cancellationToken);
            await _sms.SendAsync(from, $"USMGA request {code} received. Copilot is preparing a preview. We'll text when it's ready." + (uploadLink is null ? string.Empty : $" Upload files: {uploadLink}"), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Copilot request {Code}", code);
            record.Status = RequestStatus.Failed;
            record.LastError = ex.Message;
            record.UpdatedAt = DateTimeOffset.UtcNow;
            await _state.SaveRequestAsync(record, cancellationToken);
            await _sms.SendAsync(from, $"USMGA request {code} could not be started safely. Please contact the web admin.", cancellationToken);
        }
    }

    public async Task HandleApproveAsync(string from, string code, string approvalNonce, CancellationToken cancellationToken)
    {
        var record = await _state.GetByCodeAsync(code, cancellationToken);
        if (!ApprovalNonceValid(record, from, approvalNonce))
        {
            await _sms.SendAsync(from, "Approval rejected: invalid or expired code/nonce for this phone.", cancellationToken);
            return;
        }

        if (record!.PrNumber is null || string.IsNullOrWhiteSpace(record.ReviewedSha))
        {
            await _sms.SendAsync(from, $"Request {code} needs a fresh preview before it can be approved.", cancellationToken);
            return;
        }

        var pr = await _gitHub.GetPullRequestAsync(record.PrNumber.Value, cancellationToken);
        if (!StringComparer.OrdinalIgnoreCase.Equals(pr.HeadSha, record.ReviewedSha))
        {
            record.Status = RequestStatus.Stale;
            record.UpdatedAt = DateTimeOffset.UtcNow;
            await _state.SaveRequestAsync(record, cancellationToken);
            await _sms.SendAsync(from, $"Request {code} changed since your last preview and needs a fresh preview before publishing.", cancellationToken);
            return;
        }

        var checks = await _gitHub.GetChecksAsync(pr.HeadSha, cancellationToken);
        if (checks.State == CheckState.Pending)
        {
            await _sms.SendAsync(from, $"Request {code} checks are still running ({checks.Summary}). Reply APPROVE {code} {approvalNonce} again in a minute.", cancellationToken);
            return;
        }

        if (!checks.Passed)
        {
            record.Status = RequestStatus.Stale;
            record.UpdatedAt = DateTimeOffset.UtcNow;
            await _state.SaveRequestAsync(record, cancellationToken);
            await _sms.SendAsync(from, $"Request {code} cannot be published: checks did not pass ({checks.Summary}). Reply CHANGES {code}: to ask Copilot to fix it.", cancellationToken);
            return;
        }

        var merge = await _gitHub.MergePullRequestAsync(pr.Number, record.ReviewedSha!, cancellationToken);
        record.Status = merge.Merged ? RequestStatus.Merged : RequestStatus.Failed;
        record.LastError = merge.Merged ? null : merge.Message;
        record.UpdatedAt = DateTimeOffset.UtcNow;
        await _state.SaveRequestAsync(record, cancellationToken);
        await _sms.SendAsync(from, merge.Merged ? $"Request {code} approved and merged to production." : $"Request {code} was not merged: {merge.Message}", cancellationToken);
    }

    public async Task HandleChangesAsync(string from, string code, string changes, CancellationToken cancellationToken)
    {
        var record = await _state.GetByCodeAsync(code, cancellationToken);
        if (record is null || !StringComparer.OrdinalIgnoreCase.Equals(record.RequesterPhone, from))
        {
            await _sms.SendAsync(from, $"Request {code} was not found for this phone.", cancellationToken);
            return;
        }

        if (record.PrNumber is null)
        {
            var pr = await _gitHub.FindPullRequestAsync(record, cancellationToken);
            record.PrNumber = pr?.Number;
        }

        if (record.PrNumber is null)
        {
            await _sms.SendAsync(from, $"Request {code} does not have a PR yet. Please wait for the preview text.", cancellationToken);
            return;
        }

        await _gitHub.PostCopilotPrCommentAsync(record.PrNumber.Value, changes, cancellationToken);
        record.Status = RequestStatus.ChangesRequested;
        record.ReviewedSha = null;
        record.ApprovalNonce = null;
        record.ApprovalNonceExpiresAt = null;
        record.UpdatedAt = DateTimeOffset.UtcNow;
        await _state.SaveRequestAsync(record, cancellationToken);
        await _sms.SendAsync(from, $"Request {code}: changes sent to Copilot. We'll text a fresh preview when ready.", cancellationToken);
    }

    public async Task NotifyPreviewAsync(NotifyRequest request, CancellationToken cancellationToken)
    {
        var record = !string.IsNullOrWhiteSpace(request.Code)
            ? await _state.GetByCodeAsync(request.Code!, cancellationToken)
            : await _state.FindByIssueOrPrAsync(request.IssueNumber, request.PrNumber, cancellationToken);

        if (record is null && request.PrNumber is not null)
        {
            var issueNumber = await _gitHub.GetLinkedIssueNumberForPullRequestAsync(request.PrNumber.Value, cancellationToken);
            if (issueNumber is not null)
            {
                record = await _state.FindByIssueOrPrAsync(issueNumber, null, cancellationToken);
            }
        }

        if (record is null)
        {
            throw new InvalidOperationException("No matching request record was found.");
        }

        if (request.PrNumber is not null)
        {
            record.PrNumber = request.PrNumber;
        }

        record.PreviewUrl = request.PreviewUrl;
        record.DeployedSha = request.DeployedSha;
        record.ReviewedSha = request.DeployedSha;
        record.ApprovalNonce = _tokens.NewNonce(12);
        record.ApprovalNonceExpiresAt = DateTimeOffset.UtcNow.AddHours(24);
        record.Status = RequestStatus.PreviewDeployed;
        record.UpdatedAt = DateTimeOffset.UtcNow;
        await _state.SaveRequestAsync(record, cancellationToken);
        await _sms.SendAsync(record.RequesterPhone, $"Preview for {record.Code}: {record.PreviewUrl} Reply APPROVE {record.Code} {record.ApprovalNonce} to publish, or CHANGES {record.Code}: your requested revision.", cancellationToken);
    }

    public bool ApprovalNonceValid(RequestRecord? record, string from, string approvalNonce)
    {
        return record is not null
            && StringComparer.OrdinalIgnoreCase.Equals(record.RequesterPhone, from)
            && !string.IsNullOrWhiteSpace(record.ApprovalNonce)
            && FixedTimeEquals(record.ApprovalNonce, approvalNonce)
            && record.ApprovalNonceExpiresAt is not null
            && record.ApprovalNonceExpiresAt > DateTimeOffset.UtcNow;
    }

    private static bool FixedTimeEquals(string expected, string? actual)
    {
        if (actual is null) return false;
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private async Task<string?> MaybeCreateUploadLinkAsync(string code, string from, string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_smsOptions.UploadBaseUrl) || !_classifier.SuggestsAttachment(text))
        {
            return null;
        }

        var token = await _state.CreateUploadTokenAsync(code, from, cancellationToken);
        return $"{_smsOptions.UploadBaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(token)}";
    }

    private static string BuildIssueTitle(string code, string nonce) => $"[USMGA-SMS {code}] Website change request {nonce}";

    private static string BuildIssueBody(RequestRecord record, string? uploadLink) => $"""
SMS-driven website change request.

Request code: {record.Code}
Correlation nonce: {record.CorrelationNonce}
Requester phone: {record.RequesterPhone}
Received UTC: {record.CreatedAt:O}

Request:
{record.OriginalMessage}

Upload link issued: {uploadLink ?? "none"}

Copilot: please implement this request in the repository and open a pull request.
""";
}

public sealed class NotifyRequest
{
    public string? Code { get; set; }
    public int? IssueNumber { get; set; }
    public int? PrNumber { get; set; }
    public string PreviewUrl { get; set; } = string.Empty;
    public string DeployedSha { get; set; } = string.Empty;
}
