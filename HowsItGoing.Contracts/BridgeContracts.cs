namespace HowsItGoing.Contracts;

public enum CodexSessionStatus
{
    Running,
    Completed,
    Idle,
    Archived
}

public enum BridgeNotificationKind
{
    CodexThreadCompleted,
    GitHubPush,
    AgentThreadStarted,
    AgentRunFailed
}

public sealed record CodexSessionSummaryDto(
    string Id,
    string Title,
    string Source,
    string WorkingDirectory,
    string? GitBranch,
    string? GitOriginUrl,
    string? AgentNickname,
    string? AgentRole,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool Archived,
    CodexSessionStatus Status,
    string? LastAgentMessage,
    DateTimeOffset? CompletedAt);

public sealed record BridgeNotificationDto(
    string Id,
    BridgeNotificationKind Kind,
    string Title,
    string Message,
    DateTimeOffset OccurredAt,
    string? RelatedSessionId,
    string? RelatedRepository,
    string? RelatedUrl);

public sealed record RepositoryStatusDto(
    string? Owner,
    string? Name,
    string? DefaultBranch,
    bool IsConfigured,
    bool IsAuthenticated,
    DateTimeOffset? LastPushAt,
    string? LastPushActor,
    string? LastPushBranch,
    string? LastPushSha,
    string? LastPushMessage,
    string? LastReleaseTag,
    string? LastReleaseAssetName,
    string? LastReleaseUrl);

public sealed record StartCodexRunRequest(
    string RepoPath,
    string Prompt,
    string? Model,
    bool AllowOutsideGitRepo = true);

public sealed record StartCodexRunResponse(
    string? ThreadId,
    DateTimeOffset StartedAt,
    string RepoPath,
    string PromptPreview,
    string LaunchMode);

public sealed record UpdateInfoDto(
    bool IsUpdateAvailable,
    string? LatestTag,
    string? LatestVersionName,
    int? LatestVersionCode,
    string? AssetName,
    string? AssetDownloadUrl,
    DateTimeOffset? PublishedAt);

public sealed record BridgeSettingsDto(
    string SuggestedBridgeBaseUrl,
    string CodexHome,
    string? MonitoredRepository,
    string? GitHubRepository,
    int NotificationPollSeconds,
    int GitHubPollSeconds);
