using Android.App;
using Android.Content;
using HowsItGoing.Services;

namespace HowsItGoing.Droid;

[BroadcastReceiver(Enabled = true, Exported = true)]
[IntentFilter([Intent.ActionBootCompleted])]
public sealed class BootCompletedReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        var settings = new AppSettingsStore().LoadAsync().GetAwaiter().GetResult();
        if (settings.MonitoringEnabled)
        {
            MonitoringServiceCoordinator.SyncAsync(true).GetAwaiter().GetResult();
        }
    }
}
