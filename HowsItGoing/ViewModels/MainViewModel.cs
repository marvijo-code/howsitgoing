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
    private string _bridgeBaseUrl = "http://10.0.2.2:5217";
    private string _searchQuery = string.Empty;
    private string _selectedStatus = "All";
    private string _selectedSource = "All";
    private bool _includeArchived;
    private bool _monitoringEnabled = true;
    private string _agentRepoPath = @"C:\dev\howsitgoing";
    private string _agentPrompt = "Summarize the current repo status and stop.";
    private string _agentModel = "gpt-5.4";
    private string _statusBanner = "Idle";
    private string _repositorySummary = "Repository status not loaded yet.";
    private string _updateSummary = "No release information yet.";
    private string? _releaseUrl;
    private string _launchResult = string.Empty;

    public MainViewModel(BridgeApiClient bridgeApiClient, AppSettingsStore settingsStore)
    {
        _bridgeApiClient = bridgeApiClient;
        _settingsStore = settingsStore;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<CodexSessionSummaryDto> Sessions { get; } = [];

    public ObservableCollection<BridgeNotificationDto> Notifications { get; } = [];

    public ObservableCollection<string> StatusFilters { get; } = ["All", "Running", "Completed", "Idle", "Archived"];

    public ObservableCollection<string> SourceFilters { get; } = ["All", "vscode", "desktop", "cli"];

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
        set => SetProperty(ref _selectedStatus, value);
    }

    public string SelectedSource
    {
        get => _selectedSource;
        set => SetProperty(ref _selectedSource, value);
    }

    public bool IncludeArchived
    {
        get => _includeArchived;
        set => SetProperty(ref _includeArchived, value);
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

    public string? ReleaseUrl
    {
        get => _releaseUrl;
        private set => SetProperty(ref _releaseUrl, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settingsStore.LoadAsync(cancellationToken);
        BridgeBaseUrl = settings.BridgeBaseUrl;
        MonitoringEnabled = settings.MonitoringEnabled;
        AgentRepoPath = settings.AgentRepoPath;
        AgentModel = settings.AgentModel;
        await RefreshAsync(cancellationToken);
    }

    public async Task SaveSettingsAsync(CancellationToken cancellationToken = default)
    {
        await _settingsStore.SaveAsync(new AppSettings
        {
            BridgeBaseUrl = BridgeBaseUrl.Trim(),
            MonitoringEnabled = MonitoringEnabled,
            AgentRepoPath = AgentRepoPath.Trim(),
            AgentModel = AgentModel.Trim(),
            LastSeenNotificationAt = (await _settingsStore.LoadAsync(cancellationToken)).LastSeenNotificationAt
        }, cancellationToken);

        StatusBanner = $"Saved settings for {BridgeBaseUrl}.";
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await SaveSettingsAsync(cancellationToken);

            var sessions = await _bridgeApiClient.GetSessionsAsync(
                SearchQuery,
                SelectedStatus == "All" ? null : SelectedStatus,
                SelectedSource == "All" ? null : SelectedSource,
                IncludeArchived,
                cancellationToken);

            ReplaceCollection(Sessions, sessions);

            var notifications = await _bridgeApiClient.GetNotificationsAsync(null, 30, cancellationToken);
            ReplaceCollection(Notifications, notifications);

            var repositoryStatus = await _bridgeApiClient.GetRepositoryStatusAsync(cancellationToken);
            RepositorySummary = repositoryStatus is null
                ? "Bridge is reachable, but repository status is unavailable."
                : FormatRepositorySummary(repositoryStatus);

            var updateInfo = await _bridgeApiClient.GetUpdateInfoAsync(null, null, cancellationToken);
            UpdateSummary = updateInfo is null
                ? "No published release found yet."
                : FormatUpdateSummary(updateInfo);
            ReleaseUrl = updateInfo?.AssetDownloadUrl;

            StatusBanner = $"Loaded {Sessions.Count} Codex sessions and {Notifications.Count} notifications.";
        }
        catch (Exception ex)
        {
            StatusBanner = $"Refresh failed: {ex.Message}";
        }
    }

    public async Task StartAgentAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await SaveSettingsAsync(cancellationToken);
            var response = await _bridgeApiClient.StartAgentRunAsync(
                new StartCodexRunRequest(AgentRepoPath.Trim(), AgentPrompt.Trim(), string.IsNullOrWhiteSpace(AgentModel) ? null : AgentModel.Trim()),
                cancellationToken);

            LaunchResult = response?.ThreadId is null
                ? "Codex run launched, but the bridge did not receive a thread id before the timeout."
                : $"Started thread {response.ThreadId} in {response.RepoPath}.";
        }
        catch (Exception ex)
        {
            LaunchResult = $"Launch failed: {ex.Message}";
        }
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

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
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
