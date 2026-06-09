namespace Usmga.FunctionApp.Options;

public sealed class GitHubOptions
{
    public string Owner { get; set; } = "markjgardner";
    public string Repo { get; set; } = "usmga-webadmin";
    public string Token { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = "https://api.github.com";
    public string CopilotLogin { get; set; } = "copilot-swe-agent";
    public string CopilotAssignee { get; set; } = "copilot-swe-agent[bot]";
}

public sealed class TwilioOptions
{
    public string AccountSid { get; set; } = string.Empty;
    public string AuthToken { get; set; } = string.Empty;
    public string FromNumber { get; set; } = string.Empty;
    public string Allowlist { get; set; } = string.Empty;
    public string UploadBaseUrl { get; set; } = string.Empty;
}

public sealed class StorageOptions
{
    public string ConnectionString { get; set; } = "UseDevelopmentStorage=true";
    public string TableName { get; set; } = "SmsRequests";
}

public sealed class NotifyOptions
{
    public string SharedSecret { get; set; } = string.Empty;
    public string HeaderName { get; set; } = "x-usmga-notify-secret";
}
