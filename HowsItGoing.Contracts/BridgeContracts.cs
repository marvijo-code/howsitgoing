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
        bool allowOutsideGitRepo = true,
        string? provider = null) =>
        new(
            repoPath.Trim(),
            prompt.Trim(),
            ResolveModel(model),
            ResolveReasoningEffort(reasoningEffort),
            allowOutsideGitRepo,
            AgentProviders.Resolve(provider));
}

/// <summary>
/// Who the bridge launches for a New Run. <see cref="Codex"/> stays on the machine's own Codex
/// login; <see cref="CodexOpenRouter"/> keeps the same Codex CLI but routes the model through
/// OpenRouter with per-launch <c>-c</c> overrides, so <c>~/.codex/config.toml</c> is untouched;
/// <see cref="OpenCode"/> runs the OpenCode CLI instead.
/// </summary>
public static class AgentProviders
{
    public const string Codex = "codex";
    public const string CodexOpenRouter = "codex-openrouter";
    public const string OpenCode = "opencode";

    public static readonly IReadOnlyList<string> All = [Codex, CodexOpenRouter, OpenCode];

    public static string Resolve(string? provider) =>
        string.IsNullOrWhiteSpace(provider) ? Codex : provider.Trim().ToLowerInvariant();

    public static bool IsKnown(string provider) =>
        All.Contains(provider, StringComparer.Ordinal);

    /// <summary>
    /// Model slugs differ per provider: Codex takes a bare slug, OpenCode needs the provider prefix
    /// because it resolves the model through its own registry.
    /// </summary>
    public static string DefaultModelFor(string provider) => provider switch
    {
        CodexOpenRouter => "deepseek/deepseek-v4.1-flash",
        OpenCode => "openrouter/deepseek/deepseek-v4.1-flash",
        _ => CodexLaunchDefaults.DefaultModel
    };
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

/// <summary>
/// One page of the session feed plus the number of rows that match the same filters. The count is
/// what lets the app show "30 of 13,412" and decide whether another page exists without guessing.
/// </summary>
public sealed record SessionPageDto(
    IReadOnlyList<CodexSessionSummaryDto> Items,
    int TotalCount,
    int Offset,
    int Limit,
    string? NextCursor = null)
{
    public bool HasMore => Offset + Items.Count < TotalCount;

    /// <summary>The offset to request next, or null when the feed is exhausted.</summary>
    public int? NextOffset => HasMore ? Offset + Items.Count : null;
}

/// <summary>
/// Paging bounds and the keyset cursor shared by the app and the bridge. Offset paging costs the
/// database a scan of every skipped row (3.4s at offset 12,000), so "load more" walks from the last
/// row instead. The cursor is <c>utcTicks:id</c>; ticks are digits, so the first colon is the split.
/// </summary>
public static class SessionPaging
{
    public const int DefaultPageSize = 30;
    public const int MaxPageSize = 200;

    public static int ClampOffset(int offset) => Math.Max(0, offset);

    public static int ClampPageSize(int limit) => Math.Clamp(limit <= 0 ? DefaultPageSize : limit, 1, MaxPageSize);

    public static string CreateCursor(DateTimeOffset updatedAt, string id) =>
        $"{updatedAt.UtcTicks}:{id}";

    public static bool TryParseCursor(string? cursor, out DateTimeOffset updatedAt, out string id)
    {
        updatedAt = default;
        id = string.Empty;

        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        var separator = cursor.IndexOf(':');
        if (separator <= 0 || separator == cursor.Length - 1)
        {
            return false;
        }

        if (!long.TryParse(cursor.AsSpan(0, separator), out var ticks) || ticks < DateTimeOffset.MinValue.UtcTicks)
        {
            return false;
        }

        updatedAt = new DateTimeOffset(ticks, TimeSpan.Zero);
        id = cursor[(separator + 1)..];
        return true;
    }

    /// <summary>Pure in-memory slice used by the local (file-backed) session feed.</summary>
    public static SessionPageDto Slice(
        IReadOnlyList<CodexSessionSummaryDto> ordered,
        int offset,
        int limit)
    {
        var safeOffset = ClampOffset(offset);
        var safeLimit = ClampPageSize(limit);
        var items = ordered
            .Skip(safeOffset)
            .Take(safeLimit)
            .ToArray();

        var nextCursor = safeOffset + items.Length < ordered.Count && items.Length > 0
            ? CreateCursor(items[^1].UpdatedAt, items[^1].Id)
            : null;

        return new SessionPageDto(items, ordered.Count, safeOffset, safeLimit, nextCursor);
    }
}

public sealed record BridgeNotificationDto(
    string Id,
    BridgeNotificationKind Kind,
    string Title,
    string Message,
    DateTimeOffset OccurredAt,
    string? RelatedSessionId,
    string? RelatedRepository,
    string? RelatedUrl);

/// <summary>
/// The two browser-supplied keys of a Web Push subscription (RFC 8291), base64url without padding.
/// <paramref name="P256dh"/> is the uncompressed P-256 point of the user agent; <paramref name="Auth"/>
/// is the 16-byte authentication secret.
/// </summary>
public sealed record WebPushKeysDto(string P256dh, string Auth);

/// <summary>
/// Per-subscription delivery rules for feed pushes. Every filter is opt-in: an empty list or a null
/// value means "no restriction", so a freshly-subscribed browser receives everything until the user
/// narrows it down. Evaluated by the bridge (not the browser) so a sleeping device still gets the
/// filtering it asked for.
/// </summary>
/// <param name="Enabled">Master switch. False keeps the subscription registered but silent.</param>
/// <param name="Kinds">Notification kinds to deliver. Empty means every kind.</param>
/// <param name="Repositories">
/// Case-insensitive substring match against the notification's related repository. That field holds a
/// GitHub slug for pushes and a working directory for agent events, so a substring is the only match
/// that works for both. Empty means every repository.
/// </param>
/// <param name="Keyword">Case-insensitive substring that must appear in the title or message.</param>
/// <param name="MinIntervalSeconds">Minimum gap between pushes to this subscription. 0 disables throttling.</param>
/// <param name="QuietHoursStart">Local hour (0-23) at which pushes stop. Null disables quiet hours.</param>
/// <param name="QuietHoursEnd">Local hour (0-23) at which pushes resume. May wrap past midnight.</param>
/// <param name="UtcOffsetMinutes">The subscriber's offset from UTC, so quiet hours are evaluated in its local time.</param>
public sealed record WebPushPreferencesDto(
    bool Enabled = true,
    IReadOnlyList<BridgeNotificationKind>? Kinds = null,
    IReadOnlyList<string>? Repositories = null,
    string? Keyword = null,
    int MinIntervalSeconds = 0,
    int? QuietHoursStart = null,
    int? QuietHoursEnd = null,
    int UtcOffsetMinutes = 0);

public sealed record WebPushSubscribeRequest(
    string Endpoint,
    WebPushKeysDto Keys,
    string? Label = null,
    DateTimeOffset? ExpiresAt = null,
    WebPushPreferencesDto? Preferences = null);

public sealed record WebPushUnsubscribeRequest(string Endpoint);

/// <summary>What the browser needs before it can call <c>PushManager.subscribe</c>.</summary>
public sealed record WebPushConfigDto(
    bool IsConfigured,
    string? PublicKey,
    string? Subject);

public sealed record WebPushSubscriptionStatusDto(
    bool IsSubscribed,
    string? Label,
    WebPushPreferencesDto Preferences,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? LastPushedAt);

public sealed record WebPushSendResultDto(
    int Delivered,
    int Filtered,
    int Removed,
    IReadOnlyList<string> Errors);

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
    bool AllowOutsideGitRepo = true,
    string? Provider = null);

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
