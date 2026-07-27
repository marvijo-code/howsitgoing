using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using HowsItGoing.Contracts;
using HowsItGoing.Models;
using HowsItGoing.Services;

namespace HowsItGoing.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly BridgeApiClient _bridgeApiClient;
    private readonly AppSettingsStore _settingsStore;
    private string _bridgeBaseUrl = "http://127.0.0.1:5217";
    private string _searchQuery = string.Empty;
    private string _selectedStatus = "All";
    private string _selectedAgent = "All";
    private string _issueRepositories = string.Empty;
    private string _selectedIssueState = "All";
    private string _issueBoardSummary = "Loading issues…";
    private bool _includeArchived;
    private bool _monitoringEnabled = true;
    private string _agentRepoPath = @"C:\dev\howsitgoing";
    private string _agentPrompt = "Summarize the current repo status and stop.";
    private string _agentModel = CodexLaunchDefaults.DefaultModel;
    private string _agentReasoningEffort = CodexLaunchDefaults.DefaultReasoningEffort;
    private string _statusBanner = "Loading\u2026";
    private string _repositorySummary = string.Empty;
    private string _updateSummary = string.Empty;
    private string? _releaseUrl;
    private string _launchResult = string.Empty;
    private string _notificationWarning = string.Empty;
    private string _themePreference = "Dark";
    private int _refreshInFlight;

    /// <summary>Upper bound for one refresh pass, so the in-flight guard always clears.</summary>
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(60);

    public MainViewModel(BridgeApiClient bridgeApiClient, AppSettingsStore settingsStore)
    {
        _bridgeApiClient = bridgeApiClient;
        _settingsStore = settingsStore;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<CodexSessionSummaryDto> Sessions { get; } = [];

    public ObservableCollection<BridgeNotificationDto> Notifications { get; } = [];

    public ObservableCollection<IssueSummaryDto> Issues { get; } = [];

    public ObservableCollection<PullRequestSummaryDto> PullRequests { get; } = [];

    public ObservableCollection<string> StatusFilters { get; } = ["All", "Running", "Completed", "Idle", "Archived"];

    public ObservableCollection<string> AgentFilters { get; } = ["All", "Codex", "Claude Code", "OpenCode"];

    public ObservableCollection<string> IssueStateFilters { get; } = ["All", "Open", "Closed"];

    public string BridgeBaseUrl
    {
        get => _bridgeBaseUrl;
        set => SetProperty(ref _bridgeBaseUrl, value);
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set => SetProperty(ref _searchQuery, value);
    }

    public string SelectedStatus
    {
        get => _selectedStatus;
        set
        {
            if (SetProperty(ref _selectedStatus, value))
            {
                _ = RefreshAsync();
            }
        }
    }

    public string SelectedAgent
    {
        get => _selectedAgent;
        set
        {
            if (SetProperty(ref _selectedAgent, value))
            {
                _ = RefreshAsync();
            }
        }
    }

    public string IssueRepositories
    {
        get => _issueRepositories;
        set => SetProperty(ref _issueRepositories, value);
    }

    public string SelectedIssueState
    {
        get => _selectedIssueState;
        set
        {
            if (SetProperty(ref _selectedIssueState, value))
            {
                _ = RefreshIssuesAsync();
            }
        }
    }

    public string IssueBoardSummary
    {
        get => _issueBoardSummary;
        private set => SetProperty(ref _issueBoardSummary, value);
    }

    public bool IncludeArchived
    {
        get => _includeArchived;
        set
        {
            if (SetProperty(ref _includeArchived, value))
            {
                _ = RefreshAsync();
            }
        }
    }

    public bool MonitoringEnabled
    {
        get => _monitoringEnabled;
        set => SetProperty(ref _monitoringEnabled, value);
    }

    public string AgentRepoPath
    {
        get => _agentRepoPath;
        set => SetProperty(ref _agentRepoPath, value);
    }

    public string AgentPrompt
    {
        get => _agentPrompt;
        set => SetProperty(ref _agentPrompt, value);
    }

    public string AgentModel
    {
        get => _agentModel;
        set => SetProperty(ref _agentModel, value);
    }

    public string AgentReasoningEffort
    {
        get => _agentReasoningEffort;
        set => SetProperty(ref _agentReasoningEffort, value);
    }

    public string StatusBanner
    {
        get => _statusBanner;
        private set => SetProperty(ref _statusBanner, value);
    }

    public string RepositorySummary
    {
        get => _repositorySummary;
        private set => SetProperty(ref _repositorySummary, value);
    }

    public string UpdateSummary
    {
        get => _updateSummary;
        private set => SetProperty(ref _updateSummary, value);
    }

    public string LaunchResult
    {
        get => _launchResult;
        private set => SetProperty(ref _launchResult, value);
    }

    public string NotificationWarning
    {
        get => _notificationWarning;
        private set => SetProperty(ref _notificationWarning, value);
    }

    public string? ReleaseUrl
    {
        get => _releaseUrl;
        private set => SetProperty(ref _releaseUrl, value);
    }

    public string ThemePreference
    {
        get => _themePreference;
        private set => SetProperty(ref _themePreference, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await _settingsStore.LoadAsync(cancellationToken);
            BridgeBaseUrl = settings.BridgeBaseUrl;
            MonitoringEnabled = settings.MonitoringEnabled;
            AgentRepoPath = settings.AgentRepoPath;
            AgentModel = settings.AgentModel;
            AgentReasoningEffort = settings.AgentReasoningEffort;
            IssueRepositories = settings.IssueRepositories;
            ThemePreference = settings.ThemePreference;
            await RefreshAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            StatusBanner = $"Init failed: {ex.Message}";
        }
    }

    public async Task SaveSettingsAsync(CancellationToken cancellationToken = default)
    {
        var existing = await _settingsStore.LoadAsync(cancellationToken);
        await _settingsStore.SaveAsync(new AppSettings
        {
            BridgeBaseUrl = BridgeBaseUrl.Trim(),
            MonitoringEnabled = MonitoringEnabled,
            AgentRepoPath = AgentRepoPath.Trim(),
            AgentModel = CodexLaunchDefaults.ResolveModel(AgentModel),
            AgentReasoningEffort = CodexLaunchDefaults.ResolveReasoningEffort(AgentReasoningEffort),
            IssueRepositories = IssueRepositories.Trim(),
            ThemePreference = ThemePreference,
            LastSeenNotificationAt = existing.LastSeenNotificationAt
        }, cancellationToken);

        StatusBanner = "Settings saved.";
    }

    public async Task SaveThemePreferenceAsync(string preference, CancellationToken cancellationToken = default)
    {
        ThemePreference = preference;
        var existing = await _settingsStore.LoadAsync(cancellationToken);
        await _settingsStore.SaveAsync(existing with { ThemePreference = preference }, cancellationToken);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _refreshInFlight, 1) == 1)
        {
            return;
        }

        // A hung bridge call must never wedge the refresh loop: without this the in-flight
        // guard would block every later tick and sessions would silently stop updating.
        using var timeout = new CancellationTokenSource(RefreshTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var token = linked.Token;

        try
        {
            StatusBanner = "Refreshing\u2026";

            // Core data drives the banner, so it is awaited first and on its own. The
            // auxiliary calls below are GitHub-backed and can take tens of seconds.
            var sessions = await _bridgeApiClient.GetSessionsAsync(
                SearchQuery,
                SelectedStatus == "All" ? null : SelectedStatus,
                source: null,
                SelectedAgent == "All" ? null : AgentKinds.Normalize(SelectedAgent),
                IncludeArchived,
                token);

            SynchronizeCollection(Sessions, sessions, session => $"{session.Agent}:{session.Id}");

            var notifications = await _bridgeApiClient.GetNotificationsAsync(null, 30, token);
            SynchronizeCollection(Notifications, notifications, notification => notification.Id);

            StatusBanner = $"{Sessions.Count} sessions \u00B7 {Notifications.Count} notifications";

            // Re-read the base URL in case the BridgeApiClient auto-resolved a different one.
            var latestSettings = await _settingsStore.LoadAsync(token);
            BridgeBaseUrl = latestSettings.BridgeBaseUrl;

            await RefreshAuxiliaryAsync(token);
        }
        catch (Exception ex)
        {
            StatusBanner = timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested
                ? $"Refresh timed out after {RefreshTimeout.TotalSeconds:0}s. Is the bridge running at {BridgeBaseUrl}?"
                : FormatRequestFailure("Refresh failed", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshInFlight, 0);
        }
    }

    /// <summary>
    /// Secondary panels (repo status, update check, issue board). Each is isolated so one slow
    /// or failing GitHub call cannot blank the others or the already-published session list.
    /// </summary>
    private async Task RefreshAuxiliaryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var repositoryStatus = await _bridgeApiClient.GetRepositoryStatusAsync(cancellationToken);
            RepositorySummary = repositoryStatus is null
                ? "Bridge is reachable, but repository status is unavailable."
                : FormatRepositorySummary(repositoryStatus);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RepositorySummary = "Repository status is unavailable.";
        }

        try
        {
            var updateInfo = await _bridgeApiClient.GetUpdateInfoAsync(null, null, cancellationToken);
            UpdateSummary = updateInfo is null
                ? "No published release found yet."
                : FormatUpdateSummary(updateInfo);
            ReleaseUrl = updateInfo?.AssetDownloadUrl;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            UpdateSummary = "Update check is unavailable.";
        }

        await RefreshIssuesAsync(cancellationToken);
    }

    /// <summary>
    /// Fetches the open/closed issues + pull requests board and syncs the collections in place.
    /// Guarded so a GitHub hiccup never fails the main sessions refresh.
    /// </summary>
    public async Task RefreshIssuesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var state = SelectedIssueState == "All" ? null : SelectedIssueState.ToLowerInvariant();
            var repositories = string.IsNullOrWhiteSpace(IssueRepositories) ? null : IssueRepositories.Trim();

            var board = await _bridgeApiClient.GetIssueBoardAsync(repositories, state, cancellationToken);
            if (board is null)
            {
                Issues.Clear();
                PullRequests.Clear();
                IssueBoardSummary = "Issues are unavailable - the bridge is unreachable.";
                return;
            }

            SynchronizeCollection(Issues, board.Issues, issue => $"{issue.Repository}#{issue.Number}", IssueSignature);
            SynchronizeCollection(PullRequests, board.PullRequests, pull => $"{pull.Repository}!{pull.Number}", PullRequestSignature);
            IssueBoardSummary = FormatIssueBoardSummary(board);
        }
        catch (Exception ex)
        {
            IssueBoardSummary = FormatRequestFailure("Issues failed", ex);
        }
    }

    public async Task StartAgentAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            LaunchResult = "Starting\u2026";
            var response = await _bridgeApiClient.StartAgentRunAsync(
                CodexLaunchDefaults.CreateRequest(
                    AgentRepoPath,
                    AgentPrompt,
                    AgentModel,
                    AgentReasoningEffort),
                cancellationToken);

            LaunchResult = response?.ThreadId is null
                ? response?.LaunchMode == "shared-queue-pending"
                    ? "Run request was queued in the shared store. Keep the bridge online so it can start Codex and publish the real thread id."
                    : "Codex run launched, but no thread id was received before the timeout."
                : $"Started thread {response.ThreadId} in {response.RepoPath}.";
        }
        catch (Exception ex)
        {
            LaunchResult = FormatRequestFailure("Launch failed", ex);
        }
    }

    public async Task<bool> SendFollowUpAsync(CodexSessionSummaryDto session, string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            StatusBanner = "Type a follow-up message first.";
            return false;
        }

        try
        {
            StatusBanner = $"Sending follow-up to {AgentKinds.DisplayName(session.Agent)}…";
            var response = await _bridgeApiClient.SendFollowUpAsync(
                new SessionFollowUpRequest(session.Agent, session.Id, message.Trim(), session.WorkingDirectory),
                cancellationToken);

            StatusBanner = response is null
                ? "Follow-up sent."
                : $"Follow-up sent to {AgentKinds.DisplayName(response.Agent)} session. You'll get a notification when the turn finishes.";
            return true;
        }
        catch (Exception ex)
        {
            StatusBanner = FormatRequestFailure("Follow-up failed", ex);
            return false;
        }
    }

    public void ApplyMonitoringSyncResult(MonitoringSyncResult result) =>
        NotificationWarning = result.WarningMessage ?? string.Empty;

    private string FormatRequestFailure(string prefix, Exception exception)
    {
        if (exception is HttpRequestException || exception is TaskCanceledException)
        {
            return $"{prefix}: bridge unreachable at {BridgeBaseUrl}. If shared sync is configured, the phone can still read mirrored data and queue new runs.";
        }

        return $"{prefix}: {exception.Message}";
    }

    private static string FormatRepositorySummary(RepositoryStatusDto status)
    {
        if (!status.IsConfigured || string.IsNullOrWhiteSpace(status.Owner) || string.IsNullOrWhiteSpace(status.Name))
        {
            return "No GitHub origin is configured for the bridge repo yet.";
        }

        var pushLine = status.LastPushAt is null
            ? "No push event has been observed yet."
            : $"{status.LastPushActor} pushed {status.LastPushBranch} at {status.LastPushAt:yyyy-MM-dd HH:mm}.";

        return $"{status.Owner}/{status.Name} on {status.DefaultBranch}. {pushLine}";
    }

    private static string FormatIssueBoardSummary(IssueBoardDto board)
    {
        var openIssues = board.Repositories.Sum(r => r.OpenIssueCount);
        var closedIssues = board.Repositories.Sum(r => r.ClosedIssueCount);
        var openPulls = board.Repositories.Sum(r => r.OpenPullRequestCount);

        var errors = board.Repositories.Where(r => !string.IsNullOrWhiteSpace(r.Error)).ToList();
        if (board.Repositories.Count == 0)
        {
            return "No repositories configured. Add owner/name entries above.";
        }

        var repoNames = string.Join(", ", board.Repositories.Select(r => r.Name));
        var summary = $"{openIssues} open · {closedIssues} closed issues · {openPulls} open PRs · {repoNames}";
        if (errors.Count > 0)
        {
            summary += $" · {errors.Count} repo(s) failed to load";
        }

        return summary;
    }

    private static string FormatUpdateSummary(UpdateInfoDto updateInfo)
    {
        if (string.IsNullOrWhiteSpace(updateInfo.LatestTag))
        {
            return "No published Android release yet.";
        }

        return updateInfo.IsUpdateAvailable
            ? $"New release {updateInfo.LatestTag} is available."
            : $"Latest release is {updateInfo.LatestTag}.";
    }

    /// <summary>
    /// Applies <paramref name="values"/> to <paramref name="target"/> in place (keyed upserts,
    /// moves, and removals) so periodic refreshes don't reset the list's scroll position.
    /// </summary>
    private static string IssueSignature(IssueSummaryDto issue) =>
        $"{(int)issue.State}|{issue.Title}|{issue.UpdatedAt.UtcTicks}|{issue.ClosedAt?.UtcTicks}|" +
        $"{string.Join(",", issue.Assignees)}|{string.Join(",", issue.Labels)}|" +
        $"{string.Join(",", issue.LinkedPullRequestNumbers)}|{AgentSignature(issue.WorkingAgents)}";

    private static string PullRequestSignature(PullRequestSummaryDto pull) =>
        $"{(int)pull.State}|{pull.Title}|{pull.UpdatedAt.UtcTicks}|{(int)pull.Checks}|{pull.HeadBranch}|" +
        $"{string.Join(",", pull.LinkedIssueNumbers)}|{AgentSignature(pull.WorkingAgents)}";

    private static string AgentSignature(IReadOnlyList<AgentAssignmentDto> agents) =>
        string.Join(",", agents.Select(a => $"{a.Agent}:{a.SessionId}:{(int)a.Status}"));

    /// <param name="signatureSelector">
    /// Optional content signature. When supplied, an existing item is replaced only if its signature
    /// changed - needed for records whose members are collections (default equality never matches), so
    /// unchanged rows keep their identity and the list preserves scroll position instead of flickering.
    /// </param>
    private static void SynchronizeCollection<T>(
        ObservableCollection<T> target,
        IReadOnlyList<T> values,
        Func<T, string> keySelector,
        Func<T, string>? signatureSelector = null)
    {
        var desiredKeys = values.Select(keySelector).ToHashSet(StringComparer.Ordinal);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!desiredKeys.Contains(keySelector(target[i])))
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 0; i < values.Count; i++)
        {
            var key = keySelector(values[i]);
            var existingIndex = -1;
            for (var j = i; j < target.Count; j++)
            {
                if (string.Equals(keySelector(target[j]), key, StringComparison.Ordinal))
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                target.Insert(Math.Min(i, target.Count), values[i]);
            }
            else
            {
                if (existingIndex != i)
                {
                    target.Move(existingIndex, i);
                }

                var changed = signatureSelector is null
                    ? !EqualityComparer<T>.Default.Equals(target[i], values[i])
                    : !string.Equals(signatureSelector(target[i]), signatureSelector(values[i]), StringComparison.Ordinal);
                if (changed)
                {
                    target[i] = values[i];
                }
            }
        }

        while (target.Count > values.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
