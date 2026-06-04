using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Usmga.FunctionApp.Models;
using Usmga.FunctionApp.Options;

namespace Usmga.FunctionApp.Services;

public sealed class GitHubClient : IGitHubClient
{
    private readonly HttpClient _http;
    private readonly GitHubOptions _options;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex ClosingIssueReference = new(@"\b(?:close|closes|closed|fix|fixes|fixed|resolve|resolves|resolved)\s+#(?<number>\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BranchIssueReference = new(@"(?:^|[-_/])(?:issue|gh)?[-_/#]?(?<number>\d+)(?:$|[-_/])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public GitHubClient(HttpClient http, IOptions<GitHubOptions> options)
    {
        _http = http;
        _options = options.Value;
        _http.BaseAddress = new Uri(_options.ApiBaseUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("usmga-sms-functions");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrWhiteSpace(_options.Token))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);
        }
    }

    public async Task EnsureCopilotAssignableAsync(CancellationToken cancellationToken)
    {
        var query = "query($owner:String!,$repo:String!){repository(owner:$owner,name:$repo){suggestedActors(capabilities:[CAN_BE_ASSIGNED],first:100){nodes{login ... on Bot{id}}}}}";
        using var response = await _http.PostAsJsonAsync("graphql", new { query, variables = new { owner = _options.Owner, repo = _options.Repo } }, JsonOptions, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var nodes = doc.RootElement.GetProperty("data").GetProperty("repository").GetProperty("suggestedActors").GetProperty("nodes");
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.TryGetProperty("login", out var login) && StringComparer.OrdinalIgnoreCase.Equals(login.GetString(), _options.CopilotLogin))
            {
                return;
            }
        }
        throw new InvalidOperationException("Copilot coding agent is not assignable for this repository.");
    }

    public async Task<GitHubIssue> CreateIssueForCopilotAsync(string title, string body, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(RepoPath("issues"), new { title, body, assignees = new[] { _options.CopilotAssignee } }, JsonOptions, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = doc.RootElement;
        var assigned = false;
        if (root.TryGetProperty("assignees", out var assignees))
        {
            foreach (var assignee in assignees.EnumerateArray())
            {
                if (assignee.TryGetProperty("login", out var login) && IsCopilotLogin(login.GetString()))
                {
                    assigned = true;
                    break;
                }
            }
        }
        return new GitHubIssue(root.GetProperty("number").GetInt32(), root.GetProperty("html_url").GetString() ?? string.Empty, assigned);
    }

    public async Task<GitHubPullRequest?> FindPullRequestAsync(RequestRecord record, CancellationToken cancellationToken)
    {
        if (record.PrNumber is not null)
        {
            return await GetPullRequestAsync(record.PrNumber.Value, cancellationToken);
        }

        var search = Uri.EscapeDataString($"repo:{_options.Owner}/{_options.Repo} is:pr {record.CorrelationNonce}");
        using var response = await _http.GetAsync($"search/issues?q={search}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            var number = item.GetProperty("number").GetInt32();
            var pr = await GetPullRequestAsync(number, cancellationToken);
            if (IsCopilotPr(pr)) return pr;
        }

        if (record.IssueNumber is null) return null;
        using var timeline = await _http.GetAsync(RepoPath($"issues/{record.IssueNumber}/timeline"), cancellationToken);
        if (!timeline.IsSuccessStatusCode) return null;
        using var timelineDoc = JsonDocument.Parse(await timeline.Content.ReadAsStringAsync(cancellationToken));
        foreach (var evt in timelineDoc.RootElement.EnumerateArray())
        {
            if (evt.TryGetProperty("source", out var source) && source.TryGetProperty("issue", out var issue) && issue.TryGetProperty("pull_request", out _))
            {
                var number = issue.GetProperty("number").GetInt32();
                var pr = await GetPullRequestAsync(number, cancellationToken);
                if (IsCopilotPr(pr)) return pr;
            }
        }
        return null;
    }

    public async Task<GitHubPullRequest> GetPullRequestAsync(int prNumber, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(RepoPath($"pulls/{prNumber}"), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = doc.RootElement;
        return new GitHubPullRequest(
            root.GetProperty("number").GetInt32(),
            root.GetProperty("head").GetProperty("sha").GetString() ?? string.Empty,
            root.GetProperty("head").GetProperty("ref").GetString() ?? string.Empty,
            root.GetProperty("user").GetProperty("login").GetString() ?? string.Empty,
            root.GetProperty("html_url").GetString() ?? string.Empty,
            root.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty);
    }

    public async Task<int?> GetLinkedIssueNumberForPullRequestAsync(int prNumber, CancellationToken cancellationToken)
    {
        var pr = await GetPullRequestAsync(prNumber, cancellationToken);
        var fromText = FindLinkedIssueNumber(pr.Body, pr.HeadRef);
        if (fromText is not null) return fromText;

        // Fall back to GitHub's native PR<->issue link graph (closingIssuesReferences),
        // which the Copilot coding agent populates even when the PR body/branch do not
        // contain a regex-matchable "Closes #N" reference.
        return await GetClosingIssueNumberAsync(prNumber, cancellationToken);
    }

    private async Task<int?> GetClosingIssueNumberAsync(int prNumber, CancellationToken cancellationToken)
    {
        var query = "query($owner:String!,$repo:String!,$pr:Int!){repository(owner:$owner,name:$repo){pullRequest(number:$pr){closingIssuesReferences(first:5){nodes{number}}}}}";
        using var response = await _http.PostAsJsonAsync("graphql", new { query, variables = new { owner = _options.Owner, repo = _options.Repo, pr = prNumber } }, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Null) return null;
        var nodes = data.GetProperty("repository").GetProperty("pullRequest").GetProperty("closingIssuesReferences").GetProperty("nodes");
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.TryGetProperty("number", out var number) && number.ValueKind == JsonValueKind.Number)
            {
                return number.GetInt32();
            }
        }
        return null;
    }

    public async Task<CheckStatus> GetChecksAsync(string sha, CancellationToken cancellationToken)
    {
        using var statusResponse = await _http.GetAsync(RepoPath($"commits/{sha}/status"), cancellationToken);
        await EnsureSuccessAsync(statusResponse, cancellationToken);
        using var statusDoc = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync(cancellationToken));
        var statusRoot = statusDoc.RootElement;
        var combinedState = statusRoot.TryGetProperty("state", out var stateElement) ? stateElement.GetString() ?? "unknown" : "unknown";
        var statusCount = statusRoot.TryGetProperty("total_count", out var total) ? total.GetInt32() : 0;
        var hasFailingStatus = false;
        if (statusRoot.TryGetProperty("statuses", out var statuses))
        {
            foreach (var status in statuses.EnumerateArray())
            {
                var state = status.TryGetProperty("state", out var s) ? s.GetString() : null;
                if (StringComparer.OrdinalIgnoreCase.Equals(state, "failure") || StringComparer.OrdinalIgnoreCase.Equals(state, "error"))
                {
                    hasFailingStatus = true;
                    break;
                }
            }
        }

        using var checkRunsResponse = await _http.GetAsync(RepoPath($"commits/{sha}/check-runs"), cancellationToken);
        await EnsureSuccessAsync(checkRunsResponse, cancellationToken);
        using var checkRunsDoc = JsonDocument.Parse(await checkRunsResponse.Content.ReadAsStringAsync(cancellationToken));
        var checkRunsRoot = checkRunsDoc.RootElement;
        var checkRunCount = checkRunsRoot.TryGetProperty("total_count", out var checkTotal) ? checkTotal.GetInt32() : 0;
        var hasPendingCheckRun = false;
        var hasFailingCheckRun = false;
        if (checkRunsRoot.TryGetProperty("check_runs", out var checkRuns))
        {
            foreach (var checkRun in checkRuns.EnumerateArray())
            {
                var status = checkRun.TryGetProperty("status", out var checkStatus) ? checkStatus.GetString() : null;
                var conclusion = checkRun.TryGetProperty("conclusion", out var checkConclusion) && checkConclusion.ValueKind != JsonValueKind.Null ? checkConclusion.GetString() : null;
                if (!StringComparer.OrdinalIgnoreCase.Equals(status, "completed") || string.IsNullOrWhiteSpace(conclusion))
                {
                    hasPendingCheckRun = true;
                    continue;
                }

                if (!IsPassingCheckRunConclusion(conclusion))
                {
                    hasFailingCheckRun = true;
                }
            }
        }

        if (statusCount == 0 && checkRunCount == 0)
        {
            return new CheckStatus(CheckState.Pending, "no commit statuses or check-runs were found");
        }

        if (hasFailingStatus || hasFailingCheckRun || StringComparer.OrdinalIgnoreCase.Equals(combinedState, "failure") || StringComparer.OrdinalIgnoreCase.Equals(combinedState, "error"))
        {
            return new CheckStatus(CheckState.Failed, $"statuses={combinedState}, check-runs failed");
        }

        if (hasPendingCheckRun)
        {
            return new CheckStatus(CheckState.Pending, $"statuses={combinedState}, check-runs pending");
        }

        return new CheckStatus(CheckState.Passed, $"statuses={combinedState}, check-runs passed");
    }

    public async Task<MergeResult> MergePullRequestAsync(int prNumber, string expectedSha, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, RepoPath($"pulls/{prNumber}/merge"))
        {
            Content = new StringContent(JsonSerializer.Serialize(new { sha = expectedSha, merge_method = "squash" }, JsonOptions), Encoding.UTF8, "application/json")
        };
        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new MergeResult(false, body);
        }
        using var doc = JsonDocument.Parse(body);
        return new MergeResult(doc.RootElement.TryGetProperty("merged", out var merged) && merged.GetBoolean(), doc.RootElement.GetProperty("message").GetString() ?? string.Empty);
    }

    public async Task PostCopilotPrCommentAsync(int prNumber, string text, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(RepoPath($"issues/{prNumber}/comments"), new { body = $"@copilot {text}" }, JsonOptions, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public static int? FindLinkedIssueNumber(string? body, string? headRef)
    {
        var bodyMatch = ClosingIssueReference.Match(body ?? string.Empty);
        if (bodyMatch.Success && int.TryParse(bodyMatch.Groups["number"].Value, out var bodyIssue)) return bodyIssue;

        var branchMatch = BranchIssueReference.Match(headRef ?? string.Empty);
        if (branchMatch.Success && int.TryParse(branchMatch.Groups["number"].Value, out var branchIssue)) return branchIssue;

        return null;
    }

    private bool IsCopilotPr(GitHubPullRequest pr) => pr.HeadRef.StartsWith("copilot/", StringComparison.OrdinalIgnoreCase) && IsCopilotLogin(pr.AuthorLogin);
    private bool IsCopilotLogin(string? login) => StringComparer.OrdinalIgnoreCase.Equals(login, _options.CopilotLogin) || StringComparer.OrdinalIgnoreCase.Equals(login, _options.CopilotAssignee);
    private static bool IsPassingCheckRunConclusion(string conclusion) => conclusion.Equals("success", StringComparison.OrdinalIgnoreCase) || conclusion.Equals("neutral", StringComparison.OrdinalIgnoreCase) || conclusion.Equals("skipped", StringComparison.OrdinalIgnoreCase);
    private string RepoPath(string path) => $"repos/{_options.Owner}/{_options.Repo}/{path}";

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GitHub API failed {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}");
        }
    }
}
