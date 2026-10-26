using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using HowsItGoing.Contracts;
using HowsItGoing.Models;
using HowsItGoing.Services;

namespace HowsItGoing.Droid;

[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class BridgeMonitorForegroundService : Service
{
    private const int ForegroundNotificationId = AndroidNotifications.ForegroundNotificationId;
    private const int MinimumPollSeconds = 10;
    private const int MaximumPollSeconds = 300;
    private const ForegroundService ServiceType = ForegroundService.TypeDataSync;

    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _monitoringTask;
    private AppSettings _appliedSettings = new();

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        AndroidNotifications.EnsureChannels(this, _appliedSettings);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
        {
            StartForeground(ForegroundNotificationId, AndroidNotifications.BuildForegroundNotification(this), ServiceType);
        }
        else
        {
            StartForeground(ForegroundNotificationId, AndroidNotifications.BuildForegroundNotification(this));
        }

        if (_monitoringTask is null || _monitoringTask.IsCompleted)
        {
            _cancellationTokenSource = new CancellationTokenSource();
            _monitoringTask = MonitorAsync(_cancellationTokenSource.Token);
        }

        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        _cancellationTokenSource?.Cancel();
        base.OnDestroy();
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        var settingsStore = new AppSettingsStore();
        var sharedStoreOptions = SharedStoreOptions.FromLocalJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.Local.json"));
        if (!sharedStoreOptions.IsConfigured)
        {
            // Android packages appsettings.Local.json as an embedded resource, not a loose file.
            sharedStoreOptions = sharedStoreOptions.Merge(
                SharedStoreOptions.FromEmbeddedResource(typeof(BridgeMonitorForegroundService).Assembly, "appsettings.Local.json"));
        }
        var sharedStore = new SharedBridgeStore(sharedStoreOptions);
        var apiClient = new BridgeApiClient(settingsStore, sharedStore);

        while (!cancellationToken.IsCancellationRequested)
        {
            var pollSeconds = 30;
            try
            {
                var settings = await settingsStore.LoadAsync(cancellationToken);
                if (!settings.MonitoringEnabled)
                {
                    StopSelf();
                    return;
                }

                pollSeconds = Math.Clamp(settings.NotificationPollSeconds, MinimumPollSeconds, MaximumPollSeconds);
                if (!HasSameAlertChannels(settings))
                {
                    AndroidNotifications.EnsureChannels(this, settings);
                    _appliedSettings = settings;
                }

                if (settings.LastSeenNotificationAt is null)
                {
                    var latestNotifications = await apiClient.GetNotificationsAsync(null, 1, cancellationToken);
                    var latestSeen = latestNotifications.FirstOrDefault()?.OccurredAt ?? DateTimeOffset.UtcNow;
                    await settingsStore.SaveAsync(settings with { LastSeenNotificationAt = latestSeen }, cancellationToken);
                }
                else
                {
                    var notifications = await apiClient.GetNotificationsAsync(settings.LastSeenNotificationAt, 20, cancellationToken);
                    if (notifications.Count > 0)
                    {
                        foreach (var notification in notifications
                                     .OrderBy(x => x.OccurredAt)
                                     .Where(x => ShouldRaise(settings, x)))
                        {
                            AndroidNotifications.Show(this, settings, notification);
                        }

                        // Advance past every fetched row, including the ones filtered out here or
                        // held back by quiet hours, so they never flood in later.
                        var latestSeen = notifications.Max(x => x.OccurredAt);
                        await settingsStore.SaveAsync(settings with { LastSeenNotificationAt = latestSeen }, cancellationToken);
                    }
                }
            }
            catch
            {
                // Keep the service alive; the UI surface will show bridge errors.
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(pollSeconds), cancellationToken);
            }
            catch (System.OperationCanceledException)
            {
                return;
            }
        }
    }

    private bool HasSameAlertChannels(AppSettings settings) =>
        string.Equals(
            AndroidNotifications.GetEventChannelId(settings),
            AndroidNotifications.GetEventChannelId(_appliedSettings),
            StringComparison.Ordinal);

    /// <summary>
    /// The phone-side filter. The bridge filters web push itself, but Android alerts are raised
    /// from a polled feed, so the per-kind choice and quiet hours must be applied here.
    /// </summary>
    private static bool ShouldRaise(AppSettings settings, BridgeNotificationDto notification)
    {
        if (!IsKindEnabled(settings, notification.Kind))
        {
            return false;
        }

        return !IsQuietHour(settings, DateTimeOffset.Now);
    }

    private static bool IsKindEnabled(AppSettings settings, BridgeNotificationKind kind)
    {
        if (string.IsNullOrWhiteSpace(settings.NotificationKinds))
        {
            return true;
        }

        var kinds = settings.NotificationKinds.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return kinds.Any(value =>
            Enum.TryParse<BridgeNotificationKind>(value, ignoreCase: true, out var parsed) && parsed == kind);
    }

    private static bool IsQuietHour(AppSettings settings, DateTimeOffset now)
    {
        var start = settings.NotificationQuietStartHour;
        var end = settings.NotificationQuietEndHour;
        if (start is < 0 or > 23 || end is < 0 or > 23 || start == end)
        {
            return false;
        }

        var hour = now.Hour;
        return start < end
            ? hour >= start && hour < end
            : hour >= start || hour < end;
    }
}
