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

    /// <summary>
    /// Statuses that end a request's life. Anything else is still "in flight" and is a
    /// candidate when resolving what a bare conversational message refers to.
    /// </summary>
    /// <remarks>
    /// <see cref="Failed"/> is deliberately NOT terminal: a request can fail transiently
    /// (for example a preview callback arriving before its record was linked) and later
    /// recover, and the user will still be talking about it in the meantime.
    /// </remarks>
    public static bool IsTerminal(string status) =>
        status is Merged or Cancelled or TimedOut;
}
