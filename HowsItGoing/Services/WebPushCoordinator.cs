#if __WASM__
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;

// Uno's browser head reports its platform as "BrowserWasm", not "browser", so CA1416 flags every
// JS-interop call as unreachable-platform even though this whole file only compiles for the browser.
#pragma warning disable CA1416
#endif

namespace HowsItGoing.Services;

/// <summary>
/// A browser push subscription as the app sees it. <see cref="Endpoint"/> is the identity the bridge
/// stores against; the two keys are what it encrypts the payload with.
/// </summary>
public sealed record WebPushSubscriptionInfo(string Endpoint, string P256dh, string Auth, DateTimeOffset? ExpiresAt);

/// <summary>
/// Web push exists only in the browser head, so every other target gets an "unsupported" no-op rather
/// than forcing a compile-time split through the view model. Mirrors how
/// <see cref="MonitoringServiceCoordinator"/> keeps the Android foreground service out of shared code.
/// </summary>
public static partial class WebPushCoordinator
{
    private static bool _isSupported;

    /// <summary>
    /// False until <see cref="InitializeAsync"/> has run, because the answer needs the JS module
    /// loaded. Callers show the push UI only once this is true.
    /// </summary>
    public static bool IsSupported => _isSupported;

    /// <summary>Loads the browser module and reports whether this browser can receive push at all.</summary>
    public static async Task<bool> InitializeAsync()
    {
#if __WASM__
        try
        {
            await EnsureModuleAsync();
            _isSupported = IsSupportedCore();
        }
        catch
        {
            // A browser without the Push API, or a page served over plain HTTP from a non-localhost
            // host, simply has no push. That is a state to display, not an error to throw.
            _isSupported = false;
        }
#else
        _isSupported = false;
        await Task.CompletedTask;
#endif
        return _isSupported;
    }

    /// <summary>The browser's Notification permission: granted, denied, default, or unsupported.</summary>
    public static async Task<string> GetPermissionAsync()
    {
#if __WASM__
        await EnsureModuleAsync();
        return Permission();
#else
        return await Task.FromResult("unsupported");
#endif
    }

    /// <summary>The subscription this browser already holds, or null when it has never opted in.</summary>
    public static async Task<WebPushSubscriptionInfo?> GetSubscriptionAsync()
    {
#if __WASM__
        await EnsureModuleAsync();
        return ParseSubscription(await CurrentSubscription());
#else
        return await Task.FromResult<WebPushSubscriptionInfo?>(null);
#endif
    }

    /// <summary>
    /// Prompts for permission if needed, then subscribes against the bridge's VAPID key. Throws with a
    /// message worth showing when the browser refuses.
    /// </summary>
    public static async Task<WebPushSubscriptionInfo> SubscribeAsync(string vapidPublicKey)
    {
#if __WASM__
        await EnsureModuleAsync();
        return ParseSubscription(await Subscribe(vapidPublicKey))
            ?? throw new InvalidOperationException("The browser returned an incomplete push subscription.");
#else
        await Task.CompletedTask;
        throw new PlatformNotSupportedException("Push notifications are only available in the web app.");
#endif
    }

    /// <summary>Returns the endpoint that was dropped, or null when nothing was subscribed.</summary>
    public static async Task<string?> UnsubscribeAsync()
    {
#if __WASM__
        await EnsureModuleAsync();
        var endpoint = await Unsubscribe();
        return string.IsNullOrEmpty(endpoint) ? null : endpoint;
#else
        return await Task.FromResult<string?>(null);
#endif
    }

#if __WASM__
    private const string ModuleName = "howsitgoing-push";

    private static Task? _moduleLoad;

    private static Task EnsureModuleAsync() => _moduleLoad ??= JSHost.ImportAsync(ModuleName, "/howsitgoing-push.js");

    /// <summary>
    /// Read with JsonDocument rather than a deserialised record so nothing here depends on reflection
    /// surviving whatever trimming the browser head is published with.
    /// </summary>
    private static WebPushSubscriptionInfo? ParseSubscription(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("endpoint", out var endpoint) ||
            !root.TryGetProperty("p256dh", out var p256dh) ||
            !root.TryGetProperty("auth", out var auth) ||
            endpoint.ValueKind != JsonValueKind.String ||
            p256dh.ValueKind != JsonValueKind.String ||
            auth.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        DateTimeOffset? expiresAt = root.TryGetProperty("expiresAt", out var expiry) &&
                                    expiry.ValueKind == JsonValueKind.String &&
                                    DateTimeOffset.TryParse(expiry.GetString(), out var parsed)
            ? parsed
            : null;

        return new WebPushSubscriptionInfo(endpoint.GetString()!, p256dh.GetString()!, auth.GetString()!, expiresAt);
    }

    [SupportedOSPlatform("browser")]
    [JSImport("isSupported", ModuleName)]
    private static partial bool IsSupportedCore();

    [SupportedOSPlatform("browser")]
    [JSImport("permission", ModuleName)]
    private static partial string Permission();

    [SupportedOSPlatform("browser")]
    [JSImport("currentSubscription", ModuleName)]
    private static partial Task<string> CurrentSubscription();

    [SupportedOSPlatform("browser")]
    [JSImport("subscribe", ModuleName)]
    private static partial Task<string> Subscribe(string vapidPublicKey);

    [SupportedOSPlatform("browser")]
    [JSImport("unsubscribe", ModuleName)]
    private static partial Task<string> Unsubscribe();
#endif
}
