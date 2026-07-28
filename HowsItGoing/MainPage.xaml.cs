using HowsItGoing.Contracts;
using HowsItGoing.Services;
using HowsItGoing.ViewModels;
using Windows.UI.ViewManagement;

namespace HowsItGoing;

public sealed partial class MainPage : Page
{
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The timer ticks far more often than <see cref="AutoRefreshInterval"/> and each tick decides
    /// for itself whether a refresh is due. A tick-per-interval timer loses time it can never make
    /// up on the browser head - mobile browsers throttle background timers to about once a minute
    /// and freeze them entirely while the tab is hidden - so the feed came back stale after every
    /// unlock. Checking elapsed wall-clock instead means the first tick after a wake-up refreshes.
    /// </summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    private readonly DispatcherTimer _autoRefreshTimer = new() { Interval = TickInterval };

    private DateTimeOffset _lastRefreshStartedAt = DateTimeOffset.MinValue;
    private int _lastResumeToken;

    public MainViewModel ViewModel { get; }

    public MainPage()
    {
        this.InitializeComponent();
        ViewModel = ((App)Application.Current).Host?.Services.GetRequiredService<MainViewModel>()
            ?? throw new InvalidOperationException("MainViewModel is not registered.");
        DataContext = ViewModel;
        _autoRefreshTimer.Tick += AutoRefreshTimer_Tick;
        Loaded += MainPage_Loaded;
        Unloaded += MainPage_Unloaded;
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        HookSafeAreaInsets();
        await WebLifecycleCoordinator.InitializeAsync();
        _lastResumeToken = WebLifecycleCoordinator.ResumeToken;
        _lastRefreshStartedAt = DateTimeOffset.UtcNow;
        _autoRefreshTimer.Start();
        await ViewModel.InitializeAsync();
        await SyncMonitoringAsync();
        ApplyThemeGlyph();
        await ViewModel.InitializePushAsync();
        SyncPushToggle();
    }

    private void MainPage_Unloaded(object sender, RoutedEventArgs e) => _autoRefreshTimer.Stop();

    private async void AutoRefreshTimer_Tick(object? sender, object e)
    {
        // Keep the "updated Xs ago" readout moving on every tick, not just on the ticks that
        // refresh, so a feed that has gone quiet visibly ages instead of looking current.
        ViewModel.UpdateFeedFreshness();

        var resumeToken = WebLifecycleCoordinator.ResumeToken;
        var resumed = resumeToken != _lastResumeToken;
        _lastResumeToken = resumeToken;

        if (!resumed && DateTimeOffset.UtcNow - _lastRefreshStartedAt < AutoRefreshInterval)
        {
            return;
        }

        _lastRefreshStartedAt = DateTimeOffset.UtcNow;
        await ViewModel.RefreshAsync();
    }

    /// <summary>
    /// Android 15 / targetSdk 35 forces edge-to-edge, so the translucent status bar overlays the
    /// page. Pad the root layout by the visible-bounds insets so the header clears the bezel.
    /// </summary>
    private void HookSafeAreaInsets()
    {
        try
        {
            var view = ApplicationView.GetForCurrentView();
            view.VisibleBoundsChanged += (_, _) => ApplySafeAreaInsets();
            ApplySafeAreaInsets();
        }
        catch
        {
            // Desktop heads have no visible-bounds insets.
        }
    }

    private void ApplySafeAreaInsets()
    {
        try
        {
            var bounds = ApplicationView.GetForCurrentView().VisibleBounds;
            var topInset = Math.Max(0, bounds.Y);
            double bottomInset = 0;
            if (XamlRoot is { } xamlRoot && xamlRoot.Size.Height > 0)
            {
                bottomInset = Math.Max(0, xamlRoot.Size.Height - bounds.Bottom);
            }

            RootLayout.Padding = new Thickness(0, topInset, 0, bottomInset);
        }
        catch
        {
            // Keep the default padding when insets are unavailable.
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshAsync();
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.SaveSettingsAsync();
        await SyncMonitoringAsync();
    }

    private async void StartAgent_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.StartAgentAsync();
    }

    private async void SendFollowUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button ||
            button.DataContext is not CodexSessionSummaryDto session)
        {
            return;
        }

        var composer = FindComposerTextBox(button);
        var message = composer?.Text ?? string.Empty;
        button.IsEnabled = false;
        try
        {
            var sent = await ViewModel.SendFollowUpAsync(session, message);
            if (sent && composer is not null)
            {
                composer.Text = string.Empty;
            }
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private static TextBox? FindComposerTextBox(Button sendButton) =>
        sendButton.Parent is Grid composerRow
            ? composerRow.Children.OfType<TextBox>().FirstOrDefault()
            : null;

    private async void PushToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || !ViewModel.PushSupported)
        {
            return;
        }

        // Also fires when the view model drives IsOn; SetPushEnabledAsync no-ops when nothing changed.
        await ViewModel.SetPushEnabledAsync(toggle.IsOn);

        // A refused permission leaves the switch on while the view model says off, and the OneWay
        // binding cannot correct that on its own because the bound value never changed.
        SyncPushToggle();
    }

    private void SyncPushToggle()
    {
        if (PushToggle.IsOn != ViewModel.PushSubscribed)
        {
            PushToggle.IsOn = ViewModel.PushSubscribed;
        }
    }

    private async void SavePushPreferences_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.SavePushPreferencesAsync();
    }

    private async void SendTestPush_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.SendTestPushAsync();
    }

    private async void OpenRelease_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ViewModel.ReleaseUrl))
        {
            await ReleaseInstaller.LaunchAsync(ViewModel.ReleaseUrl);
        }
    }

    private async void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        var root = this.XamlRoot;
        if (root?.Content is FrameworkElement rootElement)
        {
            var currentTheme = rootElement.RequestedTheme;
            var newTheme = currentTheme == ElementTheme.Dark
                ? ElementTheme.Light
                : ElementTheme.Dark;
            rootElement.RequestedTheme = newTheme;

            ApplyThemeGlyph();

            // Persist the choice
            var preference = newTheme == ElementTheme.Dark ? "Dark" : "Light";
            await ViewModel.SaveThemePreferenceAsync(preference);
        }
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            int index = int.Parse(tag);
            PanelSessions.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
            PanelNotifications.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
            PanelIssues.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
            PanelAgent.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;

            SetTabVisual(TabSessions, index == 0);
            SetTabVisual(TabNotifications, index == 1);
            SetTabVisual(TabIssues, index == 2);
            SetTabVisual(TabAgent, index == 3);
        }
    }

    private async void OpenUrl_Click(object sender, RoutedEventArgs e)
    {
        var url = (sender as FrameworkElement)?.DataContext switch
        {
            IssueSummaryDto issue => issue.HtmlUrl,
            PullRequestSummaryDto pull => pull.HtmlUrl,
            _ => null
        };

        if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            await Windows.System.Launcher.LaunchUriAsync(uri);
        }
    }

    private void SetTabVisual(Button tab, bool isActive)
    {
        var foregroundKey = isActive ? "AccentBrush" : "MutedBrush";
        if (tab.Content is TextBlock tb && this.Resources.TryGetValue(foregroundKey, out var brush))
        {
            tb.Foreground = (Microsoft.UI.Xaml.Media.Brush)brush;
        }

        tab.Background = isActive && this.Resources.TryGetValue("TabActiveBackgroundBrush", out var activeBrush)
            ? (Microsoft.UI.Xaml.Media.Brush)activeBrush
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private void ApplyThemeGlyph()
    {
        var root = this.XamlRoot;
        if (root?.Content is FrameworkElement rootElement)
        {
            var isDark = rootElement.RequestedTheme == ElementTheme.Dark
                || (rootElement.RequestedTheme == ElementTheme.Default
                    && Application.Current.RequestedTheme == ApplicationTheme.Dark);

            // Sun for dark mode (tap to go light), Moon for light mode (tap to go dark)
            if (ThemeToggleButton.Content is FontIcon icon)
            {
                icon.Glyph = isDark ? "" : ""; // E706 = Brightness, E708 = ClearNight (moon)
            }
        }
    }

    private async Task SyncMonitoringAsync()
    {
        var result = await MonitoringServiceCoordinator.SyncAsync(ViewModel.MonitoringEnabled);
        ViewModel.ApplyMonitoringSyncResult(result);
    }
}
