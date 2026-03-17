namespace HowsItGoing.Services;

public static class MonitoringServiceCoordinator
{
    public static Task SyncAsync(bool enabled)
    {
#if ANDROID
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
#endif
        return Task.CompletedTask;
    }
}
