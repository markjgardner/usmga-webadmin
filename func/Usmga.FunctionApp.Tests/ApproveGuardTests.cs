using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Usmga.FunctionApp.Models;
using Usmga.FunctionApp.Options;
using Usmga.FunctionApp.Services;

namespace Usmga.FunctionApp.Tests;

public sealed class ApproveGuardTests
{
    [Fact]
    public async Task RejectsApprovalWhenPrHeadShaDiffersFromReviewedSha()
    {
        var state = new InMemoryStateStore();
        await state.CreateRequestAsync(new RequestRecord
        {
            Code = "ABC123",
            CorrelationNonce = "corr",
            RequesterPhone = "+15550000001",
            Status = RequestStatus.PreviewDeployed,
            PrNumber = 42,
            ReviewedSha = "reviewed-sha",
            ApprovalNonce = "nonce",
            ApprovalNonceExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
        }, CancellationToken.None);
        var github = new FakeGitHubClient { PullRequest = new GitHubPullRequest(42, "new-sha", "copilot/test", "copilot-swe-agent[bot]", "url"), Checks = new CheckStatus(true, "success") };
        var sms = new FakeSmsClient();
        var processor = NewProcessor(github, sms, state);

        await processor.HandleApproveAsync("+15550000001", "ABC123", "nonce", CancellationToken.None);

        var saved = await state.GetByCodeAsync("ABC123", CancellationToken.None);
        Assert.Equal(RequestStatus.Stale, saved!.Status);
        Assert.False(github.MergeCalled);
        Assert.Contains("fresh preview", sms.Messages.Single());
    }

    [Fact]
    public async Task DoesNotMergeOrMarkStaleWhenChecksStillPending()
    {
        var state = new InMemoryStateStore();
        await state.CreateRequestAsync(new RequestRecord
        {
            Code = "ABC123",
            CorrelationNonce = "corr",
            RequesterPhone = "+15550000001",
            Status = RequestStatus.PreviewDeployed,
            PrNumber = 42,
            ReviewedSha = "reviewed-sha",
            ApprovalNonce = "nonce",
            ApprovalNonceExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
        }, CancellationToken.None);
        var github = new FakeGitHubClient { PullRequest = new GitHubPullRequest(42, "reviewed-sha", "copilot/test", "copilot-swe-agent[bot]", "url"), Checks = new CheckStatus(CheckState.Pending, "check-runs pending") };
        var sms = new FakeSmsClient();
        var processor = NewProcessor(github, sms, state);

        await processor.HandleApproveAsync("+15550000001", "ABC123", "nonce", CancellationToken.None);

        var saved = await state.GetByCodeAsync("ABC123", CancellationToken.None);
        Assert.Equal(RequestStatus.PreviewDeployed, saved!.Status);
        Assert.False(github.MergeCalled);
        Assert.Contains("still running", sms.Messages.Single());
    }

    [Fact]
    public async Task RejectsApprovalWhenChecksFailedOnReviewedSha()
    {
        var state = new InMemoryStateStore();
        await state.CreateRequestAsync(new RequestRecord
        {
            Code = "ABC123",
            CorrelationNonce = "corr",
            RequesterPhone = "+15550000001",
            Status = RequestStatus.PreviewDeployed,
            PrNumber = 42,
            ReviewedSha = "reviewed-sha",
            ApprovalNonce = "nonce",
            ApprovalNonceExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
        }, CancellationToken.None);
        var github = new FakeGitHubClient { PullRequest = new GitHubPullRequest(42, "reviewed-sha", "copilot/test", "copilot-swe-agent[bot]", "url"), Checks = new CheckStatus(CheckState.Failed, "check-runs failed") };
        var sms = new FakeSmsClient();
        var processor = NewProcessor(github, sms, state);

        await processor.HandleApproveAsync("+15550000001", "ABC123", "nonce", CancellationToken.None);

        var saved = await state.GetByCodeAsync("ABC123", CancellationToken.None);
        Assert.Equal(RequestStatus.Stale, saved!.Status);
        Assert.False(github.MergeCalled);
        Assert.Contains("did not pass", sms.Messages.Single());
    }

    [Theory]
    [InlineData("+15550000002", "nonce")]
    [InlineData("+15550000001", "wrong")]
    public void ApprovalNonceValidationRequiresBoundPhoneAndNonce(string from, string nonce)
    {
        var processor = NewProcessor(new FakeGitHubClient(), new FakeSmsClient(), new InMemoryStateStore());
        var record = new RequestRecord
        {
            RequesterPhone = "+15550000001",
            ApprovalNonce = "nonce",
            ApprovalNonceExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        };

        Assert.False(processor.ApprovalNonceValid(record, from, nonce));
    }

    private static RequestProcessor NewProcessor(IGitHubClient github, ISmsClient sms, IStateStore state)
    {
        var smsOptions = Microsoft.Extensions.Options.Options.Create(new SmsOptions { Allowlist = "+15550000001" });
        return new RequestProcessor(github, sms, state, new FakeTokens(), new MessageClassifier(smsOptions), smsOptions, NullLogger<RequestProcessor>.Instance);
    }

    private sealed class FakeTokens : ITokenGenerator
    {
        public string NewRequestCode() => "ABC123";
        public string NewNonce(int bytes = 16) => "fixed-nonce";
    }

    private sealed class FakeSmsClient : ISmsClient
    {
        public List<string> Messages { get; } = new();
        public Task SendAsync(string to, string message, CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGitHubClient : IGitHubClient
    {
        public GitHubPullRequest PullRequest { get; set; } = new(42, "sha", "copilot/test", "copilot-swe-agent[bot]", "url");
        public CheckStatus Checks { get; set; } = new(true, "success");
        public bool MergeCalled { get; private set; }
        public Task EnsureCopilotAssignableAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<GitHubIssue> CreateIssueForCopilotAsync(string title, string body, CancellationToken cancellationToken) => Task.FromResult(new GitHubIssue(1, "issue"));
        public Task<GitHubPullRequest?> FindPullRequestAsync(RequestRecord record, CancellationToken cancellationToken) => Task.FromResult<GitHubPullRequest?>(PullRequest);
        public Task<GitHubPullRequest> GetPullRequestAsync(int prNumber, CancellationToken cancellationToken) => Task.FromResult(PullRequest);
        public Task<int?> GetLinkedIssueNumberForPullRequestAsync(int prNumber, CancellationToken cancellationToken) => Task.FromResult<int?>(null);
        public Task<CheckStatus> GetChecksAsync(string sha, CancellationToken cancellationToken) => Task.FromResult(Checks);
        public Task<MergeResult> MergePullRequestAsync(int prNumber, string expectedSha, CancellationToken cancellationToken)
        {
            MergeCalled = true;
            return Task.FromResult(new MergeResult(true, "merged"));
        }
        public Task PostCopilotPrCommentAsync(int prNumber, string text, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
