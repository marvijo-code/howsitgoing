using Android.App;
using Android.Content;
using HowsItGoing.Services;

namespace HowsItGoing.Droid;

[BroadcastReceiver(Enabled = false, Exported = false)]
[IntentFilter([Intent.ActionBootCompleted])]
public sealed class BootCompletedReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        // Disabled to avoid blocking app startup on broadcast delivery.
    }
}
