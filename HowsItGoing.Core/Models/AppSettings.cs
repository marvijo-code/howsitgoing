namespace HowsItGoing.Models;

public sealed record AppSettings
{
    public string BridgeBaseUrl { get; init; } = "http://127.0.0.1:5217";

    /// <summary>
    /// Shared secret sent as <c>Authorization: Bearer</c> to the bridge. Required whenever the
    /// bridge is reached over anything other than loopback (see <c>Bridge:AccessToken</c>).
    /// </summary>
    public string BridgeAccessToken { get; init; } = string.Empty;

    public bool MonitoringEnabled { get; init; } = true;

    public string AgentRepoPath { get; init; } = @"C:\dev\howsitgoing";

    public string AgentModel { get; init; } = HowsItGoing.Contracts.CodexLaunchDefaults.DefaultModel;

    /// <summary>
    /// Which harness New Run starts: <c>codex</c>, <c>codex-openrouter</c>, or <c>opencode</c>.
    /// </summary>
    public string AgentProvider { get; init; } = HowsItGoing.Contracts.AgentProviders.Codex;

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

    /// <summary>
    /// Notification sound for Android alerts: <c>default</c>, <c>none</c>, or a device
    /// <c>content://</c> ringtone URI picked in the app.
    /// </summary>
    public string NotificationSoundUri { get; init; } = "default";

    public bool NotificationVibration { get; init; } = true;

    /// <summary>"High", "Default" or "Low". High shows a heads-up banner.</summary>
    public string NotificationImportance { get; init; } = "Default";

    /// <summary>How often the foreground monitor asks the bridge for new alerts, in seconds.</summary>
    public int NotificationPollSeconds { get; init; } = 30;

    /// <summary>
    /// Comma-separated <c>BridgeNotificationKind</c> names to raise on the phone. Empty means every
    /// kind, so an upgrade never silently mutes alerts.
    /// </summary>
    public string NotificationKinds { get; init; } = string.Empty;

    /// <summary>Quiet hours start hour (0-23), or -1 to disable.</summary>
    public int NotificationQuietStartHour { get; init; } = -1;

    /// <summary>Quiet hours end hour (0-23, may wrap past midnight), or -1 to disable.</summary>
    public int NotificationQuietEndHour { get; init; } = -1;
}
