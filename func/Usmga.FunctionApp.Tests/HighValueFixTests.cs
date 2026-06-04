using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Azure.Messaging.EventGrid;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Usmga.FunctionApp.Functions;
using Usmga.FunctionApp.Models;
using Usmga.FunctionApp.Options;
using Usmga.FunctionApp.Services;

namespace Usmga.FunctionApp.Tests;

public sealed class HighValueFixTests
{
    [Fact]
    public async Task ApproveHappyPathMergesExpectedReviewedSha()
    {
        var state = new InMemoryStateStore();
        await state.CreateRequestAsync(PreviewRecord(), CancellationToken.None);
        var github = new FakeGitHubClient { PullRequest = new GitHubPullRequest(42, "reviewed-sha", "copilot/test", "copilot-swe-agent[bot]", "url"), Checks = new CheckStatus(CheckState.Passed, "ok") };
        var sms = new FakeSmsClient();

        await NewProcessor(github, sms, state).HandleApproveAsync("+15550000001", "ABC123", "nonce", CancellationToken.None);

        Assert.True(github.MergeCalled);
        Assert.Equal("reviewed-sha", github.ExpectedSha);
        Assert.Equal(RequestStatus.Merged, (await state.GetByCodeAsync("ABC123", CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task ApproveWithFailingChecksDoesNotMergeAndMarksStale()
    {
        var state = new InMemoryStateStore();
        await state.CreateRequestAsync(PreviewRecord(), CancellationToken.None);
        var github = new FakeGitHubClient { PullRequest = new GitHubPullRequest(42, "reviewed-sha", "copilot/test", "copilot-swe-agent[bot]", "url"), Checks = new CheckStatus(CheckState.Failed, "check-runs failed") };
        var sms = new FakeSmsClient();

        await NewProcessor(github, sms, state).HandleApproveAsync("+15550000001", "ABC123", "nonce", CancellationToken.None);

        Assert.False(github.MergeCalled);
        Assert.Equal(RequestStatus.Stale, (await state.GetByCodeAsync("ABC123", CancellationToken.None))!.Status);
        Assert.Contains("did not pass", sms.Messages.Single().Message);
    }

    [Fact]
    public async Task ExpiredApprovalNonceIsRejected()
    {
        var state = new InMemoryStateStore();
        var record = PreviewRecord();
        record.ApprovalNonceExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await state.CreateRequestAsync(record, CancellationToken.None);
        var github = new FakeGitHubClient();
        var sms = new FakeSmsClient();

        await NewProcessor(github, sms, state).HandleApproveAsync("+15550000001", "ABC123", "nonce", CancellationToken.None);

        Assert.False(github.MergeCalled);
        Assert.Contains("Approval rejected", sms.Messages.Single().Message);
    }

    [Fact]
    public async Task WrongPhoneOnChangesIsRejectedWithoutPrComment()
    {
        var state = new InMemoryStateStore();
        await state.CreateRequestAsync(PreviewRecord(), CancellationToken.None);
        var github = new FakeGitHubClient();
        var sms = new FakeSmsClient();

        await NewProcessor(github, sms, state).HandleChangesAsync("+15550000002", "ABC123", "make it blue", CancellationToken.None);

        Assert.False(github.CommentCalled);
        Assert.Contains("not found for this phone", sms.Messages.Single().Message);
    }

    [Fact]
    public async Task MalformedApproveIsRejectedAndDoesNotCreateIssue()
    {
        var github = new FakeGitHubClient();
        var sms = new FakeSmsClient();
        var state = new InMemoryStateStore();
        var processor = NewProcessor(github, sms, state);
        var classifier = new MessageClassifier(Microsoft.Extensions.Options.Options.Create(new SmsOptions { Allowlist = "+15550000001" }));
        var inbound = new SmsInbound(classifier, state, sms, processor, NullLogger<SmsInbound>.Instance);
        var evt = new EventGridEvent("sms", "Microsoft.Communication.SMSReceived", "1.0", BinaryData.FromObjectAsJson(new SmsReceivedPayload
        {
            MessageId = "msg-approve-bad",
            From = "+15550000001",
            To = "+15550000000",
            Message = "APPROVE abc123"
        }));

        await inbound.Run(evt, CancellationToken.None);

        Assert.Equal(0, github.CreateIssueCalls);
        Assert.Contains("APPROVE <code> <approval-nonce>", sms.Messages.Single().Message);
        Assert.Null(await state.GetByCodeAsync("ABC123", CancellationToken.None));
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("wrong", HttpStatusCode.Unauthorized)]
    public async Task NotifyRequesterRejectsMissingOrWrongSecret(string? secret, HttpStatusCode expected)
    {
        var function = NewNotifyRequester("correct");
        var request = new TestHttpRequestData(secret is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["x-usmga-notify-secret"] = secret });

        var response = await function.Run(request, CancellationToken.None);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task NotifyRequesterRejectsEmptyConfiguredSecret()
    {
        var function = NewNotifyRequester(string.Empty);
        var request = new TestHttpRequestData(new Dictionary<string, string> { ["x-usmga-notify-secret"] = "anything" });

        var response = await function.Run(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CheckRunsGuardFailsFailingCheckRun()
    {
        var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent(new { state = "success", total_count = 0, statuses = Array.Empty<object>() }) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent(new { total_count = 1, check_runs = new[] { new { status = "completed", conclusion = "failure" } } }) });
        var client = new GitHubClient(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(new GitHubOptions { ApiBaseUrl = "https://api.github.test", Token = "token" }));

        var checks = await client.GetChecksAsync("sha", CancellationToken.None);

        Assert.False(checks.Passed);
        Assert.Equal(CheckState.Failed, checks.State);
    }

    [Theory]
    [InlineData("This closes #123", "copilot/test", 123)]
    [InlineData("Fixes #456\nReady", "copilot/test", 456)]
    [InlineData("No body issue", "copilot/issue-789-test", 789)]
    public void ParsesLinkedIssueFromPrBodyOrBranch(string body, string branch, int expected)
    {
        Assert.Equal(expected, GitHubClient.FindLinkedIssueNumber(body, branch));
    }

    [Fact]
    public async Task NotifyPreviewResolvesRecordByPrLinkedIssue()
    {
        var state = new InMemoryStateStore();
        await state.CreateRequestAsync(new RequestRecord { Code = "ABC123", IssueNumber = 123, RequesterPhone = "+15550000001", CorrelationNonce = "corr" }, CancellationToken.None);
        var github = new FakeGitHubClient { LinkedIssueNumber = 123 };
        var sms = new FakeSmsClient();

        await NewProcessor(github, sms, state).NotifyPreviewAsync(new NotifyRequest { PrNumber = 42, PreviewUrl = "https://preview", DeployedSha = "sha" }, CancellationToken.None);

        var saved = await state.GetByCodeAsync("ABC123", CancellationToken.None);
        Assert.Equal(42, saved!.PrNumber);
        Assert.Equal(RequestStatus.PreviewDeployed, saved.Status);
    }

    private static RequestRecord PreviewRecord() => new()
    {
        Code = "ABC123",
        CorrelationNonce = "corr",
        RequesterPhone = "+15550000001",
        Status = RequestStatus.PreviewDeployed,
        PrNumber = 42,
        ReviewedSha = "reviewed-sha",
        ApprovalNonce = "nonce",
        ApprovalNonceExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
    };

    private static RequestProcessor NewProcessor(FakeGitHubClient github, FakeSmsClient sms, InMemoryStateStore state)
    {
        var smsOptions = Microsoft.Extensions.Options.Options.Create(new SmsOptions { Allowlist = "+15550000001,+15550000002" });
        return new RequestProcessor(github, sms, state, new FakeTokens(), new MessageClassifier(smsOptions), smsOptions, NullLogger<RequestProcessor>.Instance);
    }

    private static NotifyRequester NewNotifyRequester(string sharedSecret)
    {
        var state = new InMemoryStateStore();
        var processor = NewProcessor(new FakeGitHubClient(), new FakeSmsClient(), state);
        return new NotifyRequester(processor, Microsoft.Extensions.Options.Options.Create(new NotifyOptions { SharedSecret = sharedSecret, HeaderName = "x-usmga-notify-secret" }));
    }

    private static StringContent JsonContent(object value) => new(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json");

    private sealed class FakeTokens : ITokenGenerator
    {
        public string NewRequestCode() => "ABC123";
        public string NewNonce(int bytes = 16) => bytes == 12 ? "approval-nonce" : "fixed-nonce";
    }

    private sealed class FakeSmsClient : ISmsClient
    {
        public List<(string To, string Message)> Messages { get; } = new();
        public Task SendAsync(string to, string message, CancellationToken cancellationToken)
        {
            Messages.Add((to, message));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGitHubClient : IGitHubClient
    {
        public GitHubPullRequest PullRequest { get; set; } = new(42, "reviewed-sha", "copilot/test", "copilot-swe-agent[bot]", "url");
        public CheckStatus Checks { get; set; } = new(CheckState.Passed, "ok");
        public int? LinkedIssueNumber { get; set; }
        public bool MergeCalled { get; private set; }
        public bool CommentCalled { get; private set; }
        public string? ExpectedSha { get; private set; }
        public int CreateIssueCalls { get; private set; }
        public Task EnsureCopilotAssignableAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<GitHubIssue> CreateIssueForCopilotAsync(string title, string body, CancellationToken cancellationToken)
        {
            CreateIssueCalls++;
            return Task.FromResult(new GitHubIssue(123, "issue", true));
        }
        public Task<GitHubPullRequest?> FindPullRequestAsync(RequestRecord record, CancellationToken cancellationToken) => Task.FromResult<GitHubPullRequest?>(PullRequest);
        public Task<GitHubPullRequest> GetPullRequestAsync(int prNumber, CancellationToken cancellationToken) => Task.FromResult(PullRequest);
        public Task<int?> GetLinkedIssueNumberForPullRequestAsync(int prNumber, CancellationToken cancellationToken) => Task.FromResult(LinkedIssueNumber);
        public Task<CheckStatus> GetChecksAsync(string sha, CancellationToken cancellationToken) => Task.FromResult(Checks);
        public Task<MergeResult> MergePullRequestAsync(int prNumber, string expectedSha, CancellationToken cancellationToken)
        {
            MergeCalled = true;
            ExpectedSha = expectedSha;
            return Task.FromResult(new MergeResult(true, "merged"));
        }
        public Task PostCopilotPrCommentAsync(int prNumber, string text, CancellationToken cancellationToken)
        {
            CommentCalled = true;
            return Task.CompletedTask;
        }
    }

    private sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(_responses.Dequeue());
    }

    private sealed class TestHttpRequestData : HttpRequestData
    {
        public TestHttpRequestData(IReadOnlyDictionary<string, string> headers) : base(new TestFunctionContext())
        {
            Headers = new HttpHeadersCollection();
            foreach (var header in headers) Headers.Add(header.Key, header.Value);
        }

        public override Stream Body { get; } = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        public override HttpHeadersCollection Headers { get; }
        public override IReadOnlyCollection<IHttpCookie> Cookies { get; } = Array.Empty<IHttpCookie>();
        public override Uri Url { get; } = new("https://functions.test/api/NotifyRequester");
        public override IEnumerable<ClaimsIdentity> Identities { get; } = Array.Empty<ClaimsIdentity>();
        public override string Method { get; } = "POST";
        public override HttpResponseData CreateResponse() => new TestHttpResponseData(FunctionContext);
    }

    private sealed class TestHttpResponseData(FunctionContext context) : HttpResponseData(context)
    {
        public override HttpStatusCode StatusCode { get; set; }
        public override HttpHeadersCollection Headers { get; set; } = new();
        public override Stream Body { get; set; } = new MemoryStream();
        public override HttpCookies Cookies { get; } = null!;
    }

    private sealed class TestFunctionContext : FunctionContext
    {
        public override string InvocationId { get; } = Guid.NewGuid().ToString();
        public override string FunctionId { get; } = "test";
        public override TraceContext TraceContext { get; } = null!;
        public override BindingContext BindingContext { get; } = null!;
        public override RetryContext RetryContext { get; } = null!;
        public override IServiceProvider InstanceServices { get; set; } = null!;
        public override FunctionDefinition FunctionDefinition { get; } = null!;
        public override IDictionary<object, object> Items { get; set; } = new Dictionary<object, object>();
        public override IInvocationFeatures Features { get; } = null!;
    }
}
