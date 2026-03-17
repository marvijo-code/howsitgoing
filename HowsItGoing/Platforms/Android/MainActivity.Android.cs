using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.Content;
using Android.Views;
using HowsItGoing.Services;

namespace HowsItGoing.Droid;

[Activity(
    MainLauncher = true,
    ConfigurationChanges = global::Uno.UI.ActivityHelper.AllConfigChanges,
    WindowSoftInputMode = SoftInput.AdjustNothing | SoftInput.StateHidden
)]
public class MainActivity : Microsoft.UI.Xaml.ApplicationActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        global::AndroidX.Core.SplashScreen.SplashScreen.InstallSplashScreen(this);

        base.OnCreate(savedInstanceState);
        _ = InitializeMonitoringAsync();
    }

    private async Task InitializeMonitoringAsync()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu &&
            ContextCompat.CheckSelfPermission(this, Android.Manifest.Permission.PostNotifications) != Permission.Granted)
        {
            RequestPermissions([Android.Manifest.Permission.PostNotifications], 1200);
        }

        var settings = await new AppSettingsStore().LoadAsync();
        await MonitoringServiceCoordinator.SyncAsync(settings.MonitoringEnabled);
    }
}
