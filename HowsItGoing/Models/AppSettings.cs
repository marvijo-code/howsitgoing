namespace HowsItGoing.Models;

public sealed record AppSettings
{
    public string BridgeBaseUrl { get; init; } = "http://10.0.2.2:5217";

    public bool MonitoringEnabled { get; init; } = true;

    public string AgentRepoPath { get; init; } = @"C:\dev\howsitgoing";

    public string AgentModel { get; init; } = "gpt-5.4";

    public DateTimeOffset? LastSeenNotificationAt { get; init; }
}
