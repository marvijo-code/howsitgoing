namespace HowsItGoing.Models;

public sealed record AppSettings
{
    public string BridgeBaseUrl { get; init; } = "http://127.0.0.1:5217";

    public bool MonitoringEnabled { get; init; } = true;

    public string AgentRepoPath { get; init; } = @"C:\dev\howsitgoing";

    public string AgentModel { get; init; } = HowsItGoing.Contracts.CodexLaunchDefaults.DefaultModel;

    public string AgentReasoningEffort { get; init; } = HowsItGoing.Contracts.CodexLaunchDefaults.DefaultReasoningEffort;

    public DateTimeOffset? LastSeenNotificationAt { get; init; }

    /// <summary>
    /// Comma-separated owner/name repositories shown on the Issues board. Empty falls back to the
    /// bridge's monitored repository plus its configured <c>GitHub:IssueRepositories</c>.
    /// </summary>
    public string IssueRepositories { get; init; } = "marvijo-code/marvijo-betting-solution, marvijo-code/howsitgoing";

    /// <summary>
    /// Persisted theme preference: "Dark", "Light", or "System".
    /// </summary>
    public string ThemePreference { get; init; } = "Dark";
}
