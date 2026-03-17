namespace HowsItGoing.Services;

public static class MonitoringServiceCoordinator
{
    public static Task SyncAsync(bool enabled)
    {
#if ANDROID
        try
        {
            var context = Android.App.Application.Context;
            var intent = new Android.Content.Intent(context, typeof(HowsItGoing.Droid.BridgeMonitorForegroundService));
            if (enabled)
            {
                context.StartForegroundService(intent);
            }
            else
            {
                context.StopService(intent);
            }
        }
        catch
        {
            // UI should stay responsive even if Android refuses the service transition.
        }
#endif
        return Task.CompletedTask;
    }
}
