namespace HowsItGoing.Contracts;

public static class CodexLaunchDefaults
{
    public const string DefaultModel = "gpt-5.4";
    public const string DefaultReasoningEffort = "high";

    public static string ResolveModel(string? model) =>
        string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();

    public static string ResolveReasoningEffort(string? reasoningEffort) =>
        string.IsNullOrWhiteSpace(reasoningEffort) ? DefaultReasoningEffort : reasoningEffort.Trim();

    public static StartCodexRunRequest CreateRequest(
        string repoPath,
        string prompt,
        string? model,
        string? reasoningEffort,
        bool allowOutsideGitRepo = true) =>
        new(
            repoPath.Trim(),
            prompt.Trim(),
            ResolveModel(model),
            ResolveReasoningEffort(reasoningEffort),
            allowOutsideGitRepo);
}

public static class AgentKinds
{
    public const string Codex = "codex";
    public const string ClaudeCode = "claude";
    public const string OpenCode = "opencode";

    public static readonly IReadOnlyList<string> All = [Codex, ClaudeCode, OpenCode];

    public static string? Normalize(string? agent)
    {
        if (string.IsNullOrWhiteSpace(agent))
        {
            return null;
        }

        var trimmed = agent.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
        if (trimmed.Equals(Codex, StringComparison.OrdinalIgnoreCase))
        {
            return Codex;
        }

        if (trimmed.Equals(ClaudeCode, StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("claudecode", StringComparison.OrdinalIgnoreCase))
        {
            return ClaudeCode;
        }

        if (trimmed.Equals(OpenCode, StringComparison.OrdinalIgnoreCase))
        {
            return OpenCode;
        }

        return trimmed.ToLowerInvariant();
    }

    public static string DisplayName(string? agent) => Normalize(agent) switch
    {
        ClaudeCode => "Claude Code",
        OpenCode => "OpenCode",
        _ => "Codex"
    };
}

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
    AgentRunFailed,
    FollowUpCompleted,
    FollowUpFailed
}

public enum IssueState
{
    Open,
    Closed
}

public enum PullRequestState
{
    Open,
    Closed,
    Merged
}

/// <summary>Rolled-up CI/check state for a pull request head commit.</summary>
public enum CheckRollupStatus
{
    None,
    Pending,
    Success,
    Failure
}

/// <summary>A live agent session correlated (by repo + branch) to an issue or PR.</summary>
public sealed record AgentAssignmentDto(
    string Agent,
    string SessionId,
    string SessionTitle,
    string? Branch,
    CodexSessionStatus Status);

public sealed record PullRequestSummaryDto(
    string Repository,
    int Number,
    string Title,
    PullRequestState State,
    bool Draft,
    string? Author,
    IReadOnlyList<string> Assignees,
    string? HeadBranch,
    string? BaseBranch,
    string HtmlUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ClosedAt,
    DateTimeOffset? MergedAt,
    CheckRollupStatus Checks,
    IReadOnlyList<int> LinkedIssueNumbers,
    IReadOnlyList<AgentAssignmentDto> WorkingAgents);

public sealed record IssueSummaryDto(
    string Repository,
    int Number,
    string Title,
    IssueState State,
    string? Author,
    IReadOnlyList<string> Assignees,
    IReadOnlyList<string> Labels,
    string HtmlUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ClosedAt,
    IReadOnlyList<int> LinkedPullRequestNumbers,
    IReadOnlyList<AgentAssignmentDto> WorkingAgents);

public sealed record IssueBoardRepositoryDto(
    string Owner,
    string Name,
    bool IsConfigured,
    int OpenIssueCount,
    int ClosedIssueCount,
    int OpenPullRequestCount,
    string? Error);

public sealed record IssueBoardDto(
    IReadOnlyList<IssueBoardRepositoryDto> Repositories,
    IReadOnlyList<IssueSummaryDto> Issues,
    IReadOnlyList<PullRequestSummaryDto> PullRequests,
    DateTimeOffset GeneratedAt);

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
    DateTimeOffset? CompletedAt,
    string Agent = AgentKinds.Codex);

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
    string? ReasoningEffort = null,
    bool AllowOutsideGitRepo = true);

public sealed record StartCodexRunResponse(
    string? ThreadId,
    DateTimeOffset StartedAt,
    string RepoPath,
    string PromptPreview,
    string LaunchMode);

public sealed record SessionFollowUpRequest(
    string Agent,
    string SessionId,
    string Message,
    string? WorkingDirectory = null);

public sealed record SessionFollowUpResponse(
    string Agent,
    string SessionId,
    DateTimeOffset StartedAt,
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
