using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using HowsItGoing.Services;

namespace HowsItGoing.Droid;

[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class BridgeMonitorForegroundService : Service
{
    private const string MonitorChannelId = "howsitgoing-monitor";
    private const string EventChannelId = "howsitgoing-events";
    private const int ForegroundNotificationId = 1001;
    private const ForegroundService ServiceType = ForegroundService.TypeDataSync;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _monitoringTask;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        EnsureChannels();
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
        {
            StartForeground(ForegroundNotificationId, BuildForegroundNotification(), ServiceType);
        }
        else
        {
            StartForeground(ForegroundNotificationId, BuildForegroundNotification());
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
            try
            {
                var settings = await settingsStore.LoadAsync(cancellationToken);
                if (!settings.MonitoringEnabled)
                {
                    StopSelf();
                    return;
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
                        foreach (var notification in notifications.OrderBy(x => x.OccurredAt))
                        {
                            ShowNotification(notification);
                        }

                        var latestSeen = notifications.Max(x => x.OccurredAt);
                        await settingsStore.SaveAsync(settings with { LastSeenNotificationAt = latestSeen }, cancellationToken);
                    }
                }
            }
            catch
            {
                // Keep the service alive; the UI surface will show bridge errors.
            }

            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        }
    }

    private Notification BuildForegroundNotification() =>
        new NotificationCompat.Builder(this, MonitorChannelId)
            .SetContentTitle("How's It Going")
            .SetContentText("Monitoring Codex completions and GitHub pushes.")
            .SetSmallIcon(Resource.Mipmap.icon)
            .SetOngoing(true)
            .Build();

    private void ShowNotification(HowsItGoing.Contracts.BridgeNotificationDto notification)
    {
        var builder = new NotificationCompat.Builder(this, EventChannelId)
            .SetSmallIcon(Resource.Mipmap.icon)
            .SetContentTitle(notification.Title)
            .SetContentText(notification.Message)
            .SetStyle(new NotificationCompat.BigTextStyle().BigText(notification.Message))
            .SetAutoCancel(true)
            .SetPriority((int)NotificationPriority.Default);

        NotificationManagerCompat.From(this).Notify(notification.Id.GetHashCode(StringComparison.Ordinal), builder.Build());
    }

    private void EnsureChannels()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O)
        {
            return;
        }

        var manager = (NotificationManager?)GetSystemService(NotificationService);
        if (manager is null)
        {
            return;
        }

        var monitorChannel = new NotificationChannel(MonitorChannelId, "Bridge Monitoring", NotificationImportance.Low);
        var eventChannel = new NotificationChannel(EventChannelId, "Bridge Events", NotificationImportance.Default);
        manager.CreateNotificationChannel(monitorChannel);
        manager.CreateNotificationChannel(eventChannel);
    }
}
