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
    Task MarkPullRequestReadyForReviewAsync(string nodeId, CancellationToken cancellationToken);
    Task PostCopilotPrCommentAsync(int prNumber, string text, CancellationToken cancellationToken);
}

/// <summary>A button on an inline keyboard attached to an outbound message.</summary>
public sealed record MessageButton(string Text, string Data);

public interface IMessageChannel
{
    /// <summary>Sends a message, optionally with an inline keyboard.</summary>
    /// <returns>
    /// The provider's message id when known, so a reply to it can be correlated back to a
    /// request and its keyboard cleared later. Null when the provider did not report one.
    /// </returns>
    Task<long?> SendAsync(string chatId, string message, IReadOnlyList<MessageButton>? buttons, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the inline keyboard from a previously sent message, so a button cannot be
    /// tapped a second time after the request it referred to has been resolved.
    /// </summary>
    Task ClearButtonsAsync(string chatId, long messageId, CancellationToken cancellationToken);

    /// <summary>
    /// Acknowledges a button tap. Must be called for every callback query or the client
    /// shows a loading spinner until it times out.
    /// </summary>
    Task AcknowledgeAsync(string callbackQueryId, string? text, CancellationToken cancellationToken);
}

public static class MessageChannelExtensions
{
    public static Task<long?> SendAsync(this IMessageChannel channel, string chatId, string message, CancellationToken cancellationToken) =>
        channel.SendAsync(chatId, message, null, cancellationToken);
}

public interface IStateStore
{
    Task<bool> TryClaimMessageAsync(string messageId, CancellationToken cancellationToken);
    Task CompleteMessageAsync(string messageId, CancellationToken cancellationToken);
    Task ReleaseMessageAsync(string messageId, CancellationToken cancellationToken);
    Task CreateRequestAsync(RequestRecord record, CancellationToken cancellationToken);
    Task<RequestRecord?> GetByCodeAsync(string code, CancellationToken cancellationToken);
    Task<RequestRecord?> FindByIssueOrPrAsync(int? issueNumber, int? prNumber, CancellationToken cancellationToken);

    /// <summary>
    /// Non-terminal requests for a chat, most recently updated first. This is what lets the
    /// bot work out which request a bare "looks good" refers to without the user quoting a code.
    /// </summary>
    Task<IReadOnlyList<RequestRecord>> ListActiveForChatAsync(string chatId, CancellationToken cancellationToken);

    Task SaveRequestAsync(RequestRecord record, CancellationToken cancellationToken);
    Task<string> CreateUploadTokenAsync(string code, string requesterChatId, CancellationToken cancellationToken);
}

public interface ITokenGenerator
{
    string NewRequestCode();
    string NewNonce(int bytes = 16);
}
