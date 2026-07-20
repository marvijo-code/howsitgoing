using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AndroidX.Core.App;
using AndroidX.Core.Content;

namespace HowsItGoing.Droid;

[Activity(
    MainLauncher = true,
    ConfigurationChanges = global::Uno.UI.ActivityHelper.AllConfigChanges,
    WindowSoftInputMode = SoftInput.AdjustNothing | SoftInput.StateHidden
)]
public class MainActivity : Microsoft.UI.Xaml.ApplicationActivity
{
    private const int PostNotificationsRequestCode = 2201;
    private const string PostNotificationsPermission = "android.permission.POST_NOTIFICATIONS";
    private static TaskCompletionSource<bool>? _notificationPermissionRequest;

    public static MainActivity? CurrentActivity { get; private set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        global::AndroidX.Core.SplashScreen.SplashScreen.InstallSplashScreen(this);
        CurrentActivity = this;

        base.OnCreate(savedInstanceState);
    }

    protected override void OnDestroy()
    {
        if (ReferenceEquals(CurrentActivity, this))
        {
            CurrentActivity = null;
        }

        base.OnDestroy();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[]? permissions, Permission[]? grantResults)
    {
        if (requestCode == PostNotificationsRequestCode)
        {
            var granted = grantResults is { Length: > 0 } && grantResults[0] == Permission.Granted;
            Interlocked.Exchange(ref _notificationPermissionRequest, null)?.TrySetResult(granted);
        }

        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
    }

    internal static Task<bool> RequestNotificationPermissionAsync()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu)
        {
            return Task.FromResult(true);
        }

        var activity = CurrentActivity;
        if (activity is null)
        {
            return Task.FromResult(false);
        }

        if (ContextCompat.CheckSelfPermission(activity, PostNotificationsPermission) == Permission.Granted)
        {
            return Task.FromResult(true);
        }

        if (_notificationPermissionRequest is { Task.IsCompleted: false } pendingRequest)
        {
            return pendingRequest.Task;
        }

        var request = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _notificationPermissionRequest = request;
        ActivityCompat.RequestPermissions(activity, [PostNotificationsPermission], PostNotificationsRequestCode);
        return request.Task;
    }
}
