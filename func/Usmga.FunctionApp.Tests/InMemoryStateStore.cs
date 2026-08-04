using Usmga.FunctionApp.Models;
using Usmga.FunctionApp.Services;

namespace Usmga.FunctionApp.Tests;

internal sealed class InMemoryStateStore : IStateStore
{
    private readonly HashSet<string> _messages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RequestRecord> _records = new(StringComparer.OrdinalIgnoreCase);

    public Task<bool> TryClaimMessageAsync(string messageId, CancellationToken cancellationToken) => Task.FromResult(_messages.Add(messageId));

    public Task CompleteMessageAsync(string messageId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ReleaseMessageAsync(string messageId, CancellationToken cancellationToken)
    {
        _messages.Remove(messageId);
        return Task.CompletedTask;
    }

    public Task CreateRequestAsync(RequestRecord record, CancellationToken cancellationToken)
    {
        _records.Add(record.Code, Clone(record));
        return Task.CompletedTask;
    }

    public Task<RequestRecord?> GetByCodeAsync(string code, CancellationToken cancellationToken) => Task.FromResult(_records.TryGetValue(code, out var record) ? Clone(record) : null);

    public Task<RequestRecord?> FindByIssueOrPrAsync(int? issueNumber, int? prNumber, CancellationToken cancellationToken)
    {
        var record = _records.Values.FirstOrDefault(r => (issueNumber is not null && r.IssueNumber == issueNumber) || (prNumber is not null && r.PrNumber == prNumber));
        return Task.FromResult(record is null ? null : Clone(record));
    }

    public Task SaveRequestAsync(RequestRecord record, CancellationToken cancellationToken)
    {
        _records[record.Code] = Clone(record);
        return Task.CompletedTask;
    }

    public Task<string> CreateUploadTokenAsync(string code, string requesterChatId, CancellationToken cancellationToken) => Task.FromResult("upload-token");

    public Task<IReadOnlyList<RequestRecord>> ListActiveForChatAsync(string chatId, CancellationToken cancellationToken)
    {
        var active = _records.Values
            .Where(r => string.Equals(r.RequesterChatId, chatId, StringComparison.Ordinal) && !RequestStatus.IsTerminal(r.Status))
            .OrderByDescending(r => r.UpdatedAt)
            .Select(Clone)
            .ToList();
        return Task.FromResult<IReadOnlyList<RequestRecord>>(active);
    }

    private static RequestRecord Clone(RequestRecord r) => new()
    {
        Code = r.Code,
        CorrelationNonce = r.CorrelationNonce,
        RequesterChatId = r.RequesterChatId,
        OriginalMessage = r.OriginalMessage,
        Status = r.Status,
        IssueNumber = r.IssueNumber,
        PrNumber = r.PrNumber,
        PreviewUrl = r.PreviewUrl,
        PreviewMessageId = r.PreviewMessageId,
        ReviewedSha = r.ReviewedSha,
        DeployedSha = r.DeployedSha,
        ApprovalNonce = r.ApprovalNonce,
        ApprovalNonceExpiresAt = r.ApprovalNonceExpiresAt,
        AwaitingConfirmationUntil = r.AwaitingConfirmationUntil,
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt,
        LastError = r.LastError
    };
}
