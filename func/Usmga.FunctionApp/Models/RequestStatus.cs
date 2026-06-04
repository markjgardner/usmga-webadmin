namespace Usmga.FunctionApp.Models;

public static class RequestStatus
{
    public const string New = "new";
    public const string AgentStarted = "agent_started";
    public const string PrOpened = "pr_opened";
    public const string PreviewDeployed = "preview_deployed";
    public const string ChangesRequested = "changes_requested";
    public const string Approved = "approved";
    public const string Merged = "merged";
    public const string Failed = "failed";
    public const string Stale = "stale";
    public const string TimedOut = "timed_out";
    public const string Cancelled = "cancelled";
}
