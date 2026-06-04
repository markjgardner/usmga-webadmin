namespace Usmga.FunctionApp.Models;

public sealed record GitHubIssue(int Number, string HtmlUrl, bool CopilotAssigned)
{
    public GitHubIssue(int number, string htmlUrl) : this(number, htmlUrl, true) { }
}

public sealed record GitHubPullRequest(int Number, string HeadSha, string HeadRef, string AuthorLogin, string HtmlUrl, string Body = "");

public enum CheckState { Passed, Pending, Failed }

public sealed record CheckStatus(CheckState State, string Summary)
{
    public bool Passed => State == CheckState.Passed;
    public CheckStatus(bool passed, string summary) : this(passed ? CheckState.Passed : CheckState.Failed, summary) { }
}

public sealed record MergeResult(bool Merged, string Message);
