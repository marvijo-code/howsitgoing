namespace HowsItGoing.Services;

public sealed record MonitoringSyncResult(bool MonitoringActive, bool PermissionGranted, string? WarningMessage);

public static class MonitoringServiceCoordinator
{
    public static async Task<MonitoringSyncResult> SyncAsync(bool enabled)
    {
#if ANDROID
        try
        {
            var context = Android.App.Application.Context;
            var intent = new Android.Content.Intent(context, typeof(HowsItGoing.Droid.BridgeMonitorForegroundService));
            if (!enabled)
            {
                context.StopService(intent);
                return new MonitoringSyncResult(false, true, null);
            }

            var permissionGranted = await HowsItGoing.Droid.MainActivity.RequestNotificationPermissionAsync();
            if (!permissionGranted)
            {
                context.StopService(intent);
                return new MonitoringSyncResult(
                    false,
                    false,
                    "Android notification permission is denied. Grant notifications to receive Codex and GitHub alerts.");
            }

            if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.O)
            {
                context.StartForegroundService(intent);
            }
            else
            {
                context.StartService(intent);
            }

            return new MonitoringSyncResult(true, true, null);
        }
        catch
        {
            return new MonitoringSyncResult(
                false,
                false,
                "Android refused to start foreground monitoring. Reopen the app and check notification settings.");
        }
#else
        return await Task.FromResult(new MonitoringSyncResult(enabled, true, null));
#endif
    }
}
