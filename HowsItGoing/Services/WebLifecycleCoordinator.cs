#if __WASM__
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

// Uno's browser head reports its platform as "BrowserWasm", not "browser", so CA1416 flags every
// JS-interop call as unreachable-platform even though this whole file only compiles for the browser.
#pragma warning disable CA1416
#endif

namespace HowsItGoing.Services;

/// <summary>
/// Reports browser wake-ups (tab shown, window focused, network back) so the refresh loop can fire
/// straight away instead of waiting out an interval that a throttled or frozen tab never ran.
/// </summary>
/// <remarks>
/// Only the browser head has this problem, so every other target reports a constant token and the
/// caller's ordinary timer stays in charge - the same "no-op off-platform" shape as
/// <see cref="WebPushCoordinator"/> and <see cref="MonitoringServiceCoordinator"/>.
/// </remarks>
public static partial class WebLifecycleCoordinator
{
    /// <summary>
    /// Monotonic count of wake-ups. Callers keep the last value they saw and treat any change as
    /// "the page just came back"; the absolute value carries no meaning.
    /// </summary>
    public static int ResumeToken
    {
        get
        {
#if __WASM__
            if (!_watching)
            {
                return 0;
            }

            try
            {
                return ResumeTokenCore();
            }
            catch
            {
                // A read that fails must not kill the tick that asked for it.
                return 0;
            }
#else
            return 0;
#endif
        }
    }

    /// <summary>Starts listening for wake-ups. Safe to call on any head, and safe to call twice.</summary>
    public static async Task InitializeAsync()
    {
#if __WASM__
        if (_watching)
        {
            return;
        }

        try
        {
            await JSHost.ImportAsync(ModuleName, "/howsitgoing-lifecycle.js");
            WatchCore();
            _watching = true;
        }
        catch
        {
            // Without the module the caller still refreshes on its normal interval, just without
            // the instant-on-resume path, so a failure here is a downgrade rather than a break.
        }
#else
        await Task.CompletedTask;
#endif
    }

#if __WASM__
    private const string ModuleName = "howsitgoing-lifecycle";

    private static bool _watching;

    [SupportedOSPlatform("browser")]
    [JSImport("watch", ModuleName)]
    private static partial void WatchCore();

    [SupportedOSPlatform("browser")]
    [JSImport("resumeToken", ModuleName)]
    private static partial int ResumeTokenCore();
#endif
}
