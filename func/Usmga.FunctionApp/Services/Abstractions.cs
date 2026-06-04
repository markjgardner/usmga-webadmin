using Usmga.FunctionApp.Models;

namespace Usmga.FunctionApp.Services;

public interface IGitHubClient
{
    Task EnsureCopilotAssignableAsync(CancellationToken cancellationToken);
    Task<GitHubIssue> CreateIssueForCopilotAsync(string title, string body, CancellationToken cancellationToken);
    Task<GitHubPullRequest?> FindPullRequestAsync(RequestRecord record, CancellationToken cancellationToken);
    Task<GitHubPullRequest> GetPullRequestAsync(int prNumber, CancellationToken cancellationToken);
    Task<int?> GetLinkedIssueNumberForPullRequestAsync(int prNumber, CancellationToken cancellationToken);
    Task<CheckStatus> GetChecksAsync(string sha, CancellationToken cancellationToken);
    Task<MergeResult> MergePullRequestAsync(int prNumber, string expectedSha, CancellationToken cancellationToken);
    Task PostCopilotPrCommentAsync(int prNumber, string text, CancellationToken cancellationToken);
}

public interface ISmsClient
{
    Task SendAsync(string to, string message, CancellationToken cancellationToken);
}

public interface IStateStore
{
    Task<bool> TryClaimMessageAsync(string messageId, CancellationToken cancellationToken);
    Task CompleteMessageAsync(string messageId, CancellationToken cancellationToken);
    Task ReleaseMessageAsync(string messageId, CancellationToken cancellationToken);
    Task CreateRequestAsync(RequestRecord record, CancellationToken cancellationToken);
    Task<RequestRecord?> GetByCodeAsync(string code, CancellationToken cancellationToken);
    Task<RequestRecord?> FindByIssueOrPrAsync(int? issueNumber, int? prNumber, CancellationToken cancellationToken);
    Task SaveRequestAsync(RequestRecord record, CancellationToken cancellationToken);
    Task<string> CreateUploadTokenAsync(string code, string requesterPhone, CancellationToken cancellationToken);
}

public interface ITokenGenerator
{
    string NewRequestCode();
    string NewNonce(int bytes = 16);
}
