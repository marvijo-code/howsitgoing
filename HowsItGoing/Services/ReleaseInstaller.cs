namespace HowsItGoing.Services;

public static class ReleaseInstaller
{
    public static async Task LaunchAsync(string downloadUrl)
    {
#if ANDROID
        var context = Android.App.Application.Context;
        using var httpClient = new HttpClient();
        var targetPath = Path.Combine(context.CacheDir?.AbsolutePath ?? Path.GetTempPath(), "howsitgoing-latest.apk");

        await using (var remoteStream = await httpClient.GetStreamAsync(downloadUrl))
        await using (var localStream = File.Create(targetPath))
        {
            await remoteStream.CopyToAsync(localStream);
        }

        var apkFile = new Java.IO.File(targetPath);
        var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(context, $"{context.PackageName}.fileprovider", apkFile);
        var intent = new Android.Content.Intent(Android.Content.Intent.ActionView);
        intent.SetDataAndType(uri, "application/vnd.android.package-archive");
        intent.AddFlags(Android.Content.ActivityFlags.NewTask | Android.Content.ActivityFlags.GrantReadUriPermission);
        context.StartActivity(intent);
#else
        await Windows.System.Launcher.LaunchUriAsync(new Uri(downloadUrl));
#endif
    }
}
