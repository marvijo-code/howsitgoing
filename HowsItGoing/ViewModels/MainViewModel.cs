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
    private readonly FeedCacheStore _feedCache;
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
    private string _agentProvider = AgentProviders.Codex;
    private string _statusBanner = "Loading\u2026";
    private string _feedFreshness = string.Empty;
    private bool _isRefreshing;
    private DateTimeOffset? _lastRefreshedAt;
    private string _repositorySummary = string.Empty;
    private string _updateSummary = string.Empty;
    private string? _releaseUrl;
    private string _launchResult = string.Empty;
    private string _notificationWarning = string.Empty;
    private string _themePreference = "Dark";
    private int _refreshInFlight;
    private int _sessionTotalCount;
    private string? _sessionCursor;
    private bool _isLoadingMoreSessions;
    private DateTimeOffset _lastIssuesRefreshAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAuxiliaryRefreshAt = DateTimeOffset.MinValue;
    private bool _pushSupported;
    private bool _pushSubscribed;
    private string _pushStatus = "Checking push support…";
    private bool _pushOnCodexCompleted = true;
    private bool _pushOnGitHubPush = true;
    private bool _pushOnAgentStarted = true;
    private bool _pushOnAgentFailed = true;
    private bool _pushOnFollowUpCompleted = true;
    private bool _pushOnFollowUpFailed = true;
    private string _pushRepositories = string.Empty;
    private string _pushKeyword = string.Empty;
    private string _pushMinIntervalSeconds = "0";
    private string _pushQuietHoursStart = string.Empty;
    private string _pushQuietHoursEnd = string.Empty;
    private string? _pushEndpoint;
    private string? _vapidPublicKey;

    // ---- Android system notifications -----------------------------------------------------------
    private string _notificationSoundKey = NotificationSoundCatalog.DefaultKey;
    private bool _notificationVibration = true;
    private string _notificationImportance = "Default";
    private string _notificationPollSeconds = "30";
    private string _notificationQuietStart = string.Empty;
    private string _notificationQuietEnd = string.Empty;
    private string _notificationSettingsStatus = string.Empty;
    private bool _androidOnCodexCompleted = true;
    private bool _androidOnGitHubPush = true;
    private bool _androidOnAgentStarted = true;
    private bool _androidOnAgentFailed = true;
    private bool _androidOnFollowUpCompleted = true;
    private bool _androidOnFollowUpFailed = true;

    /// <summary>Upper bound for one refresh pass, so the in-flight guard always clears.</summary>
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Cards in the Sessions list are paginated for a reason: an ItemsControl realises every card,
    /// and 180 rich cards once locked the phone's UI thread long enough for Android to kill the app.
    /// </summary>
    private const int SessionPageSize = SessionPaging.DefaultPageSize;

    /// <summary>
    /// Ceiling on what one refresh re-reads. A user who has paged deep gets their loaded window
    /// refreshed, but the 30s tick never becomes a 13k-row download.
    /// </summary>
    private const int MaxSessionsRefreshed = 120;

    public MainViewModel(BridgeApiClient bridgeApiClient, AppSettingsStore settingsStore, FeedCacheStore feedCache)
    {
        _bridgeApiClient = bridgeApiClient;
        _settingsStore = settingsStore;
        _feedCache = feedCache;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<CodexSessionSummaryDto> Sessions { get; } = [];

    public ObservableCollection<BridgeNotificationDto> Notifications { get; } = [];

    public ObservableCollection<IssueSummaryDto> Issues { get; } = [];

    public ObservableCollection<PullRequestSummaryDto> PullRequests { get; } = [];

    public ObservableCollection<string> StatusFilters { get; } = ["All", "Running", "Completed", "Idle", "Archived"];

    public ObservableCollection<string> AgentFilters { get; } = ["All", "Codex", "Claude Code", "OpenCode"];

    public ObservableCollection<string> IssueStateFilters { get; } = ["All", "Open", "Closed"];

    public ObservableCollection<NotificationSoundOption> NotificationSounds { get; } = [];

    private NotificationSoundOption? _selectedNotificationSound;

    /// <summary>
    /// The picker binds SelectedItem: Uno does not reliably re-resolve a SelectedValue/Key pair
    /// when the item list is filled after the binding is evaluated, which left the box blank.
    /// </summary>
    public NotificationSoundOption? SelectedNotificationSound
    {
        get => _selectedNotificationSound;
        set
        {
            if (SetProperty(ref _selectedNotificationSound, value) && value is not null)
            {
                NotificationSoundKey = value.Key;
            }
        }
    }

    public ObservableCollection<string> NotificationImportances { get; } = ["High", "Default", "Low"];

    public ObservableCollection<string> NotificationPollOptions { get; } = ["10", "15", "30", "60", "120", "300"];

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
                _ = RefreshAsync(resetPaging: true);
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
                _ = RefreshAsync(resetPaging: true);
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
                _ = RefreshAsync(resetPaging: true);
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

    /// <summary>
    /// The harness New Run launches. Switching provider also swaps in that provider's model slug
    /// when the box still holds another provider's default, so a tap never sends a Codex slug to
    /// OpenCode or vice versa.
    /// </summary>
    public string AgentProvider
    {
        get => _agentProvider;
        set
        {
            var resolved = AgentProviders.Resolve(value);
            if (!AgentProviders.IsKnown(resolved))
            {
                return;
            }

            if (!SetProperty(ref _agentProvider, resolved))
            {
                return;
            }

            var previousDefaults = AgentProviders.All
                .Select(AgentProviders.DefaultModelFor)
                .ToList();
            if (previousDefaults.Contains(AgentModel.Trim(), StringComparer.Ordinal))
            {
                AgentModel = AgentProviders.DefaultModelFor(resolved);
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AgentProviderLabel)));
        }
    }

    public string AgentProviderLabel => AgentProvider switch
    {
        AgentProviders.CodexOpenRouter => "Codex on OpenRouter",
        AgentProviders.OpenCode => "OpenCode",
        _ => "Codex (subscription)"
    };

    public string StatusBanner
    {
        get => _statusBanner;
        private set => SetProperty(ref _statusBanner, value);
    }

    /// <summary>
    /// How long ago the feed last loaded, e.g. "updated 12s ago". Driven by the page's tick rather
    /// than by refreshes so a feed that has stopped updating visibly ages instead of sitting on a
    /// stale "just now" - the symptom that made the browser head look alive when it was not.
    /// </summary>
    public string FeedFreshness
    {
        get => _feedFreshness;
        private set => SetProperty(ref _feedFreshness, value);
    }

    /// <summary>True while a refresh is in flight; drives the header's live indicator.</summary>
    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set => SetProperty(ref _isRefreshing, value);
    }

    /// <summary>Recomputes <see cref="FeedFreshness"/> from the clock. Cheap; call it on every tick.</summary>
    public void UpdateFeedFreshness()
    {
        if (_lastRefreshedAt is not { } refreshedAt)
        {
            FeedFreshness = string.Empty;
            return;
        }

        FeedFreshness = Contracts.FeedFreshness.Describe(DateTimeOffset.UtcNow - refreshedAt);
    }

    /// <summary>Rows matching the current filters, not just the rows loaded so far.</summary>
    public int SessionTotalCount
    {
        get => _sessionTotalCount;
        private set
        {
            if (SetProperty(ref _sessionTotalCount, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasMoreSessions)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SessionFeedSummary)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LoadMoreSessionsLabel)));
            }
        }
    }

    public bool IsLoadingMoreSessions
    {
        get => _isLoadingMoreSessions;
        private set
        {
            if (SetProperty(ref _isLoadingMoreSessions, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LoadMoreSessionsLabel)));
            }
        }
    }

    public bool HasMoreSessions => Sessions.Count < SessionTotalCount;

    public string SessionFeedSummary => SessionTotalCount > Sessions.Count
        ? $"{Sessions.Count:N0} of {SessionTotalCount:N0} sessions"
        : $"{SessionTotalCount:N0} sessions";

    public string LoadMoreSessionsLabel => IsLoadingMoreSessions
        ? "Loading\u2026"
        : HasMoreSessions
            ? $"Load more ({Sessions.Count:N0} of {SessionTotalCount:N0})"
            : "All sessions loaded";

    /// <summary>True while the Issues tab is the one on screen; the GitHub board only refreshes then.</summary>
    public bool IssuesTabActive { get; set; }

    /// <summary>
    /// Set by the page while the user is reading further down the feed. A 30s tick must not yank
    /// the list back to the newest card under their thumb; the next tick after they return to the
    /// top (or an explicit Refresh) publishes again.
    /// </summary>
    public bool PauseSessionUpdates { get; set; }

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

    /// <summary>True only in the browser head, once the Push API has been confirmed available.</summary>
    public bool PushSupported
    {
        get => _pushSupported;
        private set => SetProperty(ref _pushSupported, value);
    }

    public bool PushSubscribed
    {
        get => _pushSubscribed;
        private set => SetProperty(ref _pushSubscribed, value);
    }

    public string PushStatus
    {
        get => _pushStatus;
        private set => SetProperty(ref _pushStatus, value);
    }

    public bool PushOnCodexCompleted
    {
        get => _pushOnCodexCompleted;
        set => SetProperty(ref _pushOnCodexCompleted, value);
    }

    public bool PushOnGitHubPush
    {
        get => _pushOnGitHubPush;
        set => SetProperty(ref _pushOnGitHubPush, value);
    }

    public bool PushOnAgentStarted
    {
        get => _pushOnAgentStarted;
        set => SetProperty(ref _pushOnAgentStarted, value);
    }

    public bool PushOnAgentFailed
    {
        get => _pushOnAgentFailed;
        set => SetProperty(ref _pushOnAgentFailed, value);
    }

    public bool PushOnFollowUpCompleted
    {
        get => _pushOnFollowUpCompleted;
        set => SetProperty(ref _pushOnFollowUpCompleted, value);
    }

    public bool PushOnFollowUpFailed
    {
        get => _pushOnFollowUpFailed;
        set => SetProperty(ref _pushOnFollowUpFailed, value);
    }

    /// <summary>Comma-separated repository filters; empty means every repository.</summary>
    public string PushRepositories
    {
        get => _pushRepositories;
        set => SetProperty(ref _pushRepositories, value);
    }

    public string PushKeyword
    {
        get => _pushKeyword;
        set => SetProperty(ref _pushKeyword, value);
    }

    public string PushMinIntervalSeconds
    {
        get => _pushMinIntervalSeconds;
        set => SetProperty(ref _pushMinIntervalSeconds, value);
    }

    public string PushQuietHoursStart
    {
        get => _pushQuietHoursStart;
        set => SetProperty(ref _pushQuietHoursStart, value);
    }

    public string PushQuietHoursEnd
    {
        get => _pushQuietHoursEnd;
        set => SetProperty(ref _pushQuietHoursEnd, value);
    }

    // ---- Android system notifications -----------------------------------------------------------

    /// <summary>True only on the Android head; the settings card hides everywhere else.</summary>
    public bool SystemNotificationsSupported
    {
        get
        {
#if ANDROID
            return true;
#else
            return false;
#endif
        }
    }

    public string NotificationSoundKey
    {
        get => _notificationSoundKey;
        set => SetProperty(ref _notificationSoundKey, value);
    }

    public bool NotificationVibration
    {
        get => _notificationVibration;
        set => SetProperty(ref _notificationVibration, value);
    }

    public string NotificationImportance
    {
        get => _notificationImportance;
        set => SetProperty(ref _notificationImportance, value);
    }

    public string NotificationPollSeconds
    {
        get => _notificationPollSeconds;
        set => SetProperty(ref _notificationPollSeconds, value);
    }

    /// <summary>Local hour (0-23) quiet hours start, or blank to disable.</summary>
    public string NotificationQuietHoursStart
    {
        get => _notificationQuietStart;
        set => SetProperty(ref _notificationQuietStart, value);
    }

    public string NotificationQuietHoursEnd
    {
        get => _notificationQuietEnd;
        set => SetProperty(ref _notificationQuietEnd, value);
    }

    public string NotificationSettingsStatus
    {
        get => _notificationSettingsStatus;
        private set => SetProperty(ref _notificationSettingsStatus, value);
    }

    public bool AndroidOnCodexCompleted
    {
        get => _androidOnCodexCompleted;
        set => SetProperty(ref _androidOnCodexCompleted, value);
    }

    public bool AndroidOnGitHubPush
    {
        get => _androidOnGitHubPush;
        set => SetProperty(ref _androidOnGitHubPush, value);
    }

    public bool AndroidOnAgentStarted
    {
        get => _androidOnAgentStarted;
        set => SetProperty(ref _androidOnAgentStarted, value);
    }

    public bool AndroidOnAgentFailed
    {
        get => _androidOnAgentFailed;
        set => SetProperty(ref _androidOnAgentFailed, value);
    }

    public bool AndroidOnFollowUpCompleted
    {
        get => _androidOnFollowUpCompleted;
        set => SetProperty(ref _androidOnFollowUpCompleted, value);
    }

    public bool AndroidOnFollowUpFailed
    {
        get => _androidOnFollowUpFailed;
        set => SetProperty(ref _androidOnFollowUpFailed, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await _settingsStore.LoadAsync(cancellationToken);
            BridgeBaseUrl = settings.BridgeBaseUrl;
            MonitoringEnabled = settings.MonitoringEnabled;
            AgentRepoPath = settings.AgentRepoPath;
            AgentProvider = settings.AgentProvider;
            AgentModel = settings.AgentModel;
            AgentReasoningEffort = settings.AgentReasoningEffort;
            IssueRepositories = settings.IssueRepositories;
            ThemePreference = settings.ThemePreference;
            ApplyNotificationSettings(settings);

            // Seed the picker with the two always-present choices; device tones are enumerated
            // later, off the UI thread, the first time the Alerts tab opens.
            if (NotificationSounds.Count == 0)
            {
                foreach (var option in NotificationSoundCatalog.FixedOptions())
                {
                    NotificationSounds.Add(option);
                }
            }

            SelectNotificationSound();

            // Paint the last known feed immediately, then replace it with a live one. On a phone
            // this is the difference between a blank "Loading…" and a usable list in under a second.
            ApplyCachedFeed(await _feedCache.LoadAsync(cancellationToken));

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
        var updated = new AppSettings
        {
            BridgeBaseUrl = BridgeBaseUrl.Trim(),
            MonitoringEnabled = MonitoringEnabled,
            AgentRepoPath = AgentRepoPath.Trim(),
            AgentModel = CodexLaunchDefaults.ResolveModel(AgentModel),
            AgentReasoningEffort = CodexLaunchDefaults.ResolveReasoningEffort(AgentReasoningEffort),
            AgentProvider = AgentProvider,
            IssueRepositories = IssueRepositories.Trim(),
            ThemePreference = ThemePreference,
            LastSeenNotificationAt = existing.LastSeenNotificationAt
        };
        await _settingsStore.SaveAsync(WithNotificationSettings(updated), cancellationToken);

        StatusBanner = "Settings saved.";
    }

    /// <summary>
    /// Saves the Android alert card and restarts monitoring so the new sound, vibration or poll
    /// interval takes effect immediately instead of at the next reboot.
    /// </summary>
    public async Task SaveNotificationSettingsAsync(CancellationToken cancellationToken = default)
    {
        var existing = await _settingsStore.LoadAsync(cancellationToken);
        var updated = WithNotificationSettings(existing with { MonitoringEnabled = MonitoringEnabled });
        await _settingsStore.SaveAsync(updated, cancellationToken);

        var result = await MonitoringServiceCoordinator.SyncAsync(updated.MonitoringEnabled);
        ApplyMonitoringSyncResult(result);

        NotificationSettingsStatus = result.WarningMessage is { Length: > 0 }
            ? result.WarningMessage
            : "Alert settings saved. The next alert uses them.";
    }

    /// <summary>
    /// Fills the sound picker with the device's notification tones the first time the Alerts tab is
    /// shown. The media-provider query is slow enough to trip an ANR if it runs on the UI thread, so
    /// the fixed choices are already there and the device list arrives when it arrives.
    /// </summary>
    public async Task EnsureNotificationSoundsAsync(CancellationToken cancellationToken = default)
    {
        if (NotificationSounds.Count > NotificationSoundCatalog.FixedOptions().Count)
        {
            return;
        }

        try
        {
            var options = await Task.Run(NotificationSoundCatalog.Load, cancellationToken);
            foreach (var option in options)
            {
                if (!NotificationSounds.Any(existing => existing.Key == option.Key))
                {
                    NotificationSounds.Add(option);
                }
            }

            SelectNotificationSound();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NotificationSettingsStatus = $"Could not read device notification tones: {ex.Message}";
        }
    }

    private void SelectNotificationSound()
    {
        SelectedNotificationSound = NotificationSounds.FirstOrDefault(option =>
                string.Equals(option.Key, NotificationSoundKey, StringComparison.Ordinal))
            ?? NotificationSounds.FirstOrDefault();
    }

    /// <summary>Fires one notification through the exact channel the real alerts use.</summary>
    public void SendTestNotification()
    {
#if ANDROID
        try
        {
            var settings = WithNotificationSettings(new AppSettings { MonitoringEnabled = MonitoringEnabled });
            HowsItGoing.Droid.AndroidNotifications.ShowTest(Android.App.Application.Context, settings);
            NotificationSettingsStatus = "Test alert sent. Check the notification shade.";
        }
        catch (Exception ex)
        {
            NotificationSettingsStatus = $"Test alert failed: {ex.Message}";
        }
#else
        NotificationSettingsStatus = "System notifications are Android only.";
#endif
    }

    private void ApplyNotificationSettings(AppSettings settings)
    {
        NotificationSoundKey = string.IsNullOrWhiteSpace(settings.NotificationSoundUri)
            ? NotificationSoundCatalog.DefaultKey
            : settings.NotificationSoundUri;
        NotificationVibration = settings.NotificationVibration;
        NotificationImportance = string.IsNullOrWhiteSpace(settings.NotificationImportance)
            ? "Default"
            : settings.NotificationImportance;
        NotificationPollSeconds = Math.Clamp(settings.NotificationPollSeconds, 10, 300).ToString();

        var kinds = string.IsNullOrWhiteSpace(settings.NotificationKinds)
            ? null
            : settings.NotificationKinds
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => Enum.TryParse<BridgeNotificationKind>(value, ignoreCase: true, out var parsed) ? parsed : (BridgeNotificationKind?)null)
                .Where(kind => kind is not null)
                .Select(kind => kind!.Value)
                .ToHashSet();

        AndroidOnCodexCompleted = kinds is null || kinds.Contains(BridgeNotificationKind.CodexThreadCompleted);
        AndroidOnGitHubPush = kinds is null || kinds.Contains(BridgeNotificationKind.GitHubPush);
        AndroidOnAgentStarted = kinds is null || kinds.Contains(BridgeNotificationKind.AgentThreadStarted);
        AndroidOnAgentFailed = kinds is null || kinds.Contains(BridgeNotificationKind.AgentRunFailed);
        AndroidOnFollowUpCompleted = kinds is null || kinds.Contains(BridgeNotificationKind.FollowUpCompleted);
        AndroidOnFollowUpFailed = kinds is null || kinds.Contains(BridgeNotificationKind.FollowUpFailed);

        NotificationQuietHoursStart = settings.NotificationQuietStartHour >= 0
            ? settings.NotificationQuietStartHour.ToString()
            : string.Empty;
        NotificationQuietHoursEnd = settings.NotificationQuietEndHour >= 0
            ? settings.NotificationQuietEndHour.ToString()
            : string.Empty;
    }

    private AppSettings WithNotificationSettings(AppSettings settings)
    {
        var kinds = SelectedNotificationKinds();
        return settings with
        {
            NotificationSoundUri = NotificationSoundKey,
            NotificationVibration = NotificationVibration,
            NotificationImportance = NotificationImportances.Contains(NotificationImportance)
                ? NotificationImportance
                : "Default",
            NotificationPollSeconds = ParseBoundedInt(NotificationPollSeconds, 30, 10, 300),
            NotificationKinds = kinds.Count == 6
                ? string.Empty
                : kinds.Count == 0
                    ? "none"
                    : string.Join(",", kinds.Select(kind => kind.ToString())),
            NotificationQuietStartHour = ParseHour(NotificationQuietHoursStart) ?? -1,
            NotificationQuietEndHour = ParseHour(NotificationQuietHoursEnd) ?? -1
        };
    }

    private List<BridgeNotificationKind> SelectedNotificationKinds()
    {
        var kinds = new List<BridgeNotificationKind>(6);
        if (AndroidOnCodexCompleted)
        {
            kinds.Add(BridgeNotificationKind.CodexThreadCompleted);
        }

        if (AndroidOnGitHubPush)
        {
            kinds.Add(BridgeNotificationKind.GitHubPush);
        }

        if (AndroidOnAgentStarted)
        {
            kinds.Add(BridgeNotificationKind.AgentThreadStarted);
        }

        if (AndroidOnAgentFailed)
        {
            kinds.Add(BridgeNotificationKind.AgentRunFailed);
        }

        if (AndroidOnFollowUpCompleted)
        {
            kinds.Add(BridgeNotificationKind.FollowUpCompleted);
        }

        if (AndroidOnFollowUpFailed)
        {
            kinds.Add(BridgeNotificationKind.FollowUpFailed);
        }

        return kinds;
    }

    public async Task SaveThemePreferenceAsync(string preference, CancellationToken cancellationToken = default)
    {
        ThemePreference = preference;
        var existing = await _settingsStore.LoadAsync(cancellationToken);
        await _settingsStore.SaveAsync(existing with { ThemePreference = preference }, cancellationToken);
    }

    // ---- Web push for the feed -----------------------------------------------------------------

    /// <summary>
    /// Brings up the push card: loads the browser module, reads back whatever this browser is already
    /// subscribed to, and mirrors the bridge-side preferences into the editable properties. Safe to
    /// call on every head - it simply reports "unsupported" outside the browser.
    /// </summary>
    public async Task InitializePushAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            PushSupported = await WebPushCoordinator.InitializeAsync();
            if (!PushSupported)
            {
                PushStatus = "Push notifications need the web app in a browser that supports the Push API.";
                return;
            }

            var config = await _bridgeApiClient.GetPushConfigAsync(cancellationToken);
            if (config is null || !config.IsConfigured || string.IsNullOrWhiteSpace(config.PublicKey))
            {
                PushStatus = "The bridge is not offering push notifications right now.";
                PushSupported = false;
                return;
            }

            _vapidPublicKey = config.PublicKey;

            var subscription = await WebPushCoordinator.GetSubscriptionAsync();
            if (subscription is null)
            {
                PushSubscribed = false;
                PushStatus = await WebPushCoordinator.GetPermissionAsync() == "denied"
                    ? "This browser has blocked notifications. Allow them in site settings to enable push."
                    : "Push is off. Turn it on to get feed alerts when the app is closed.";
                return;
            }

            _pushEndpoint = subscription.Endpoint;
            var status = await _bridgeApiClient.GetPushSubscriptionAsync(subscription.Endpoint, cancellationToken);

            if (status is null || !status.IsSubscribed)
            {
                // The browser still holds a subscription the bridge has forgotten (a pruned endpoint,
                // or a different bridge). Re-register it so the two sides agree again.
                await SavePushSubscriptionAsync(subscription, BuildPreferences(), cancellationToken);
                return;
            }

            ApplyPreferences(status.Preferences);
            PushSubscribed = true;
            PushStatus = FormatPushStatus(status);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PushStatus = FormatRequestFailure("Push setup failed", ex);
        }
    }

    /// <summary>Subscribes or unsubscribes this browser, driven by the toggle in the Alerts panel.</summary>
    public async Task SetPushEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (!PushSupported || enabled == PushSubscribed)
        {
            return;
        }

        try
        {
            if (!enabled)
            {
                var removed = await WebPushCoordinator.UnsubscribeAsync() ?? _pushEndpoint;
                if (!string.IsNullOrEmpty(removed))
                {
                    await _bridgeApiClient.RemovePushSubscriptionAsync(removed, cancellationToken);
                }

                _pushEndpoint = null;
                PushSubscribed = false;
                PushStatus = "Push is off.";
                return;
            }

            if (string.IsNullOrWhiteSpace(_vapidPublicKey))
            {
                PushStatus = "The bridge has not supplied a push key yet. Refresh and try again.";
                return;
            }

            PushStatus = "Asking the browser for permission…";
            var subscription = await WebPushCoordinator.SubscribeAsync(_vapidPublicKey);
            _pushEndpoint = subscription.Endpoint;
            await SavePushSubscriptionAsync(subscription, BuildPreferences(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PushSubscribed = false;
            PushStatus = FormatRequestFailure("Push failed", ex);
        }
    }

    /// <summary>Pushes the edited filters to the bridge, which is where they are actually applied.</summary>
    public async Task SavePushPreferencesAsync(CancellationToken cancellationToken = default)
    {
        if (!PushSubscribed || string.IsNullOrEmpty(_pushEndpoint))
        {
            PushStatus = "Turn push on before saving filters.";
            return;
        }

        var kinds = SelectedPushKinds();
        if (kinds.Count == 0)
        {
            // An empty list means "everything" on the wire, so saving one here would silently do the
            // opposite of what unticking every box looks like.
            PushStatus = "Pick at least one alert type, or switch push off.";
            return;
        }

        try
        {
            var subscription = await WebPushCoordinator.GetSubscriptionAsync();
            if (subscription is null)
            {
                PushSubscribed = false;
                PushStatus = "The browser dropped this push subscription. Turn push on again.";
                return;
            }

            await SavePushSubscriptionAsync(subscription, BuildPreferences(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PushStatus = FormatRequestFailure("Saving push filters failed", ex);
        }
    }

    /// <summary>Sends one push straight past the filters, so the user can prove the plumbing works.</summary>
    public async Task SendTestPushAsync(CancellationToken cancellationToken = default)
    {
        if (!PushSubscribed || string.IsNullOrEmpty(_pushEndpoint))
        {
            PushStatus = "Turn push on before sending a test.";
            return;
        }

        try
        {
            PushStatus = "Sending a test push…";
            var result = await _bridgeApiClient.SendTestPushAsync(_pushEndpoint, cancellationToken);

            PushStatus = result switch
            {
                null => "The bridge did not answer the test push.",
                { Delivered: > 0 } => "Test push sent - it should appear as a system notification.",
                { Removed: > 0 } => "The push service rejected this subscription. Turn push off and on again.",
                { Errors.Count: > 0 } => $"Test push failed: {result.Errors[0]}",
                _ => "The test push was not delivered."
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PushStatus = FormatRequestFailure("Test push failed", ex);
        }
    }

    private async Task SavePushSubscriptionAsync(
        WebPushSubscriptionInfo subscription,
        WebPushPreferencesDto preferences,
        CancellationToken cancellationToken)
    {
        var status = await _bridgeApiClient.SavePushSubscriptionAsync(
            new WebPushSubscribeRequest(
                subscription.Endpoint,
                new WebPushKeysDto(subscription.P256dh, subscription.Auth),
                Label: "Web app",
                subscription.ExpiresAt,
                preferences),
            cancellationToken);

        _pushEndpoint = subscription.Endpoint;
        PushSubscribed = status?.IsSubscribed ?? false;
        PushStatus = status is null
            ? "The bridge did not confirm the push subscription."
            : FormatPushStatus(status);
    }

    private WebPushPreferencesDto BuildPreferences()
    {
        var repositories = PushRepositories
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new WebPushPreferencesDto(
            Enabled: true,
            Kinds: SelectedPushKinds(),
            Repositories: repositories,
            Keyword: string.IsNullOrWhiteSpace(PushKeyword) ? null : PushKeyword.Trim(),
            MinIntervalSeconds: ParseBoundedInt(PushMinIntervalSeconds, 0, 0, 86_400),
            QuietHoursStart: ParseHour(PushQuietHoursStart),
            QuietHoursEnd: ParseHour(PushQuietHoursEnd),
            // The bridge has no idea where the browser is, so quiet hours travel with the offset.
            UtcOffsetMinutes: (int)DateTimeOffset.Now.Offset.TotalMinutes);
    }

    private void ApplyPreferences(WebPushPreferencesDto preferences)
    {
        var kinds = preferences.Kinds;
        var all = kinds is null || kinds.Count == 0;

        PushOnCodexCompleted = all || kinds!.Contains(BridgeNotificationKind.CodexThreadCompleted);
        PushOnGitHubPush = all || kinds!.Contains(BridgeNotificationKind.GitHubPush);
        PushOnAgentStarted = all || kinds!.Contains(BridgeNotificationKind.AgentThreadStarted);
        PushOnAgentFailed = all || kinds!.Contains(BridgeNotificationKind.AgentRunFailed);
        PushOnFollowUpCompleted = all || kinds!.Contains(BridgeNotificationKind.FollowUpCompleted);
        PushOnFollowUpFailed = all || kinds!.Contains(BridgeNotificationKind.FollowUpFailed);

        PushRepositories = preferences.Repositories is { Count: > 0 } repositories
            ? string.Join(", ", repositories)
            : string.Empty;
        PushKeyword = preferences.Keyword ?? string.Empty;
        PushMinIntervalSeconds = preferences.MinIntervalSeconds.ToString();
        PushQuietHoursStart = preferences.QuietHoursStart?.ToString() ?? string.Empty;
        PushQuietHoursEnd = preferences.QuietHoursEnd?.ToString() ?? string.Empty;
    }

    private List<BridgeNotificationKind> SelectedPushKinds()
    {
        var kinds = new List<BridgeNotificationKind>();
        if (PushOnCodexCompleted)
        {
            kinds.Add(BridgeNotificationKind.CodexThreadCompleted);
        }

        if (PushOnGitHubPush)
        {
            kinds.Add(BridgeNotificationKind.GitHubPush);
        }

        if (PushOnAgentStarted)
        {
            kinds.Add(BridgeNotificationKind.AgentThreadStarted);
        }

        if (PushOnAgentFailed)
        {
            kinds.Add(BridgeNotificationKind.AgentRunFailed);
        }

        if (PushOnFollowUpCompleted)
        {
            kinds.Add(BridgeNotificationKind.FollowUpCompleted);
        }

        if (PushOnFollowUpFailed)
        {
            kinds.Add(BridgeNotificationKind.FollowUpFailed);
        }

        return kinds;
    }

    private static string FormatPushStatus(WebPushSubscriptionStatusDto status)
    {
        if (!status.IsSubscribed)
        {
            return "Push is off.";
        }

        var parts = new List<string>();

        var kinds = status.Preferences.Kinds;
        parts.Add(kinds is null || kinds.Count is 0 or 6 ? "all alerts" : $"{kinds.Count} alert types");

        if (status.Preferences.Repositories is { Count: > 0 } repositories)
        {
            parts.Add($"repos: {string.Join(", ", repositories)}");
        }

        if (!string.IsNullOrWhiteSpace(status.Preferences.Keyword))
        {
            parts.Add($"keyword “{status.Preferences.Keyword}”");
        }

        if (status.Preferences is { QuietHoursStart: { } quietStart, QuietHoursEnd: { } quietEnd })
        {
            parts.Add($"quiet {quietStart:00}:00-{quietEnd:00}:00");
        }

        if (status.Preferences.MinIntervalSeconds > 0)
        {
            parts.Add($"at most every {status.Preferences.MinIntervalSeconds}s");
        }

        return $"Push is on · {string.Join(" · ", parts)}";
    }

    private static int ParseBoundedInt(string value, int fallback, int minimum, int maximum) =>
        int.TryParse(value, out var parsed) ? Math.Clamp(parsed, minimum, maximum) : fallback;

    private static int? ParseHour(string value) =>
        int.TryParse(value, out var parsed) && parsed is >= 0 and <= 23 ? parsed : null;

    /// <param name="resetPaging">
    /// A filter change or an explicit Refresh starts from the newest page. The 30s tick passes
    /// false, so a user who has paged back keeps their place.
    /// </param>
    public async Task RefreshAsync(CancellationToken cancellationToken = default, bool resetPaging = false)
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
            IsRefreshing = true;
            StatusBanner = "Refreshing\u2026";

            if (resetPaging)
            {
                Sessions.Clear();
                SessionTotalCount = 0;
                _sessionCursor = null;
                _lastRefreshedAt = null;
            }

            // Core data drives the banner, so it is awaited first and on its own. The
            // auxiliary calls below are GitHub-backed and can take tens of seconds.
            var requestedLimit = SessionPaging.ClampPageSize(
                Math.Max(SessionPageSize, Math.Min(Sessions.Count, MaxSessionsRefreshed)));
            var page = await _bridgeApiClient.GetSessionsPageAsync(
                SearchQuery,
                SelectedStatus == "All" ? null : SelectedStatus,
                source: null,
                SelectedAgent == "All" ? null : AgentKinds.Normalize(SelectedAgent),
                IncludeArchived,
                offset: 0,
                limit: requestedLimit,
                token);

            if (page is null)
            {
                throw new InvalidOperationException("The bridge returned no session page.");
            }

            // While the user is scrolled into older cards, publish the count but leave the rows
            // alone: replacing them resets the list under their thumb every 30 seconds.
            if (resetPaging || !PauseSessionUpdates)
            {
                SynchronizeCollection(Sessions, page.Items, session => $"{session.Agent}:{session.Id}");
                _sessionCursor = page.NextCursor;
            }

            SessionTotalCount = page.TotalCount;

            // Publish what the user came for as soon as the sessions land. Notifications are a large
            // payload and the auxiliary GitHub calls are slower still, so waiting for them left the
            // header on "Refreshing…" for over a minute on a phone.
            StatusBanner = SessionFeedSummary;
            _lastRefreshedAt = DateTimeOffset.UtcNow;
            UpdateFeedFreshness();

            var notifications = await _bridgeApiClient.GetNotificationsAsync(null, 30, token);
            SynchronizeCollection(Notifications, notifications, notification => notification.Id);

            // Stamped once the feed itself is published, not after the auxiliary GitHub calls, so
            // the readout tracks the session list the user is actually looking at.
            _lastRefreshedAt = DateTimeOffset.UtcNow;
            UpdateFeedFreshness();
            // "alerts" rather than "notifications" to match the tab of the same name, and because
            // the shorter word keeps this on one line next to the freshness readout on a phone.
            StatusBanner = $"{SessionFeedSummary} \u00B7 {Notifications.Count} alerts";

            await SaveFeedCacheAsync(page, notifications, token);

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
            IsRefreshing = false;
            Interlocked.Exchange(ref _refreshInFlight, 0);
        }
    }

    /// <summary>
    /// Appends the next page. Called by the list when it nears the bottom and by the Load more
    /// button, so the feed can be walked back through all 13k mirrored sessions a page at a time.
    /// </summary>
    public async Task LoadMoreSessionsAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoadingMoreSessions || !HasMoreSessions)
        {
            return;
        }

        IsLoadingMoreSessions = true;
        try
        {
            var offset = Sessions.Count;
            var page = await _bridgeApiClient.GetSessionsPageAsync(
                SearchQuery,
                SelectedStatus == "All" ? null : SelectedStatus,
                source: null,
                SelectedAgent == "All" ? null : AgentKinds.Normalize(SelectedAgent),
                IncludeArchived,
                offset,
                SessionPageSize,
                cancellationToken,
                cursor: _sessionCursor);

            if (page is null)
            {
                StatusBanner = "Could not load more sessions - the bridge is unreachable.";
                return;
            }

            var known = Sessions
                .Select(session => $"{session.Agent}:{session.Id}")
                .ToHashSet(StringComparer.Ordinal);
            var added = 0;
            foreach (var session in page.Items)
            {
                if (known.Add($"{session.Agent}:{session.Id}"))
                {
                    Sessions.Add(session);
                    added++;
                }
            }

            // A page can overlap the loaded rows when the feed shifts under us; take the server's
            // count so "x of y" stays honest either way.
            SessionTotalCount = page.TotalCount;
            _sessionCursor = page.NextCursor;
            StatusBanner = added > 0
                ? $"{SessionFeedSummary} \u00B7 {Notifications.Count} alerts"
                : SessionTotalCount <= Sessions.Count
                    ? $"{SessionFeedSummary} \u00B7 {Notifications.Count} alerts"
                    : $"No new sessions on this page. Try Refresh.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusBanner = FormatRequestFailure("Loading more sessions failed", ex);
        }
        finally
        {
            IsLoadingMoreSessions = false;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasMoreSessions)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SessionFeedSummary)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LoadMoreSessionsLabel)));
        }
    }

    private void ApplyCachedFeed(FeedSnapshot? snapshot)
    {
        if (snapshot?.Sessions is not { Items.Count: > 0 } page)
        {
            return;
        }

        SynchronizeCollection(Sessions, page.Items, session => $"{session.Agent}:{session.Id}");
        SessionTotalCount = page.TotalCount;
        SynchronizeCollection(Notifications, snapshot.Notifications, notification => notification.Id);

        _lastRefreshedAt = snapshot.SavedAt;
        UpdateFeedFreshness();
        StatusBanner = $"Cached {SessionFeedSummary} \u00B7 refreshing\u2026";
    }

    private async Task SaveFeedCacheAsync(
        SessionPageDto page,
        IReadOnlyList<BridgeNotificationDto> notifications,
        CancellationToken cancellationToken)
    {
        // Cache the first page only; deeper pages are re-fetchable and the point is a fast launch.
        var firstPage = page.Offset == 0
            ? page
            : new SessionPageDto(
                page.Items.Take(SessionPageSize).ToArray(),
                page.TotalCount,
                0,
                SessionPageSize);

        await _feedCache.SaveAsync(
            new FeedSnapshot(DateTimeOffset.UtcNow, firstPage, notifications.ToArray()),
            cancellationToken);
    }

    /// <summary>
    /// Secondary panels (repo status, update check, issue board). Each is isolated so one slow
    /// or failing GitHub call cannot blank the others or the already-published session list.
    /// These are GitHub-backed and change slowly, so they are throttled instead of riding every
    /// 30s tick: repository/update at most every 5 minutes, and the issue board only while its tab
    /// is open (plus one warm-up load so the first visit is instant).
    /// </summary>
    private async Task RefreshAuxiliaryAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastAuxiliaryRefreshAt == DateTimeOffset.MinValue ||
            now - _lastAuxiliaryRefreshAt >= TimeSpan.FromMinutes(5))
        {
            _lastAuxiliaryRefreshAt = now;

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
        }

        if (IssuesTabActive || _lastIssuesRefreshAt == DateTimeOffset.MinValue)
        {
            await RefreshIssuesAsync(cancellationToken);
        }
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
        finally
        {
            _lastIssuesRefreshAt = DateTimeOffset.UtcNow;
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
                    AgentReasoningEffort,
                    provider: AgentProvider),
                cancellationToken);

            LaunchResult = response is null
                ? "Launch returned no response."
                : AgentProvider == AgentProviders.OpenCode
                    ? $"Started an OpenCode run in {response.RepoPath}. It appears in Sessions once OpenCode registers it."
                    : response.ThreadId is null
                        ? response.LaunchMode == "shared-queue-pending"
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
