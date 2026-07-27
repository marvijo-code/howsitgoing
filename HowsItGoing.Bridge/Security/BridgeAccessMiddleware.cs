using System.Net;
using System.Security.Cryptography;
using System.Text;
using HowsItGoing.Bridge.Services;
using Microsoft.Extensions.Options;

namespace HowsItGoing.Bridge.Security;

/// <summary>
/// Gate on every <c>/api</c> request.
///
/// The bridge reads local agent transcripts and launches agents with approval prompts disabled, so
/// an unauthenticated caller reaching it is equivalent to a shell on the machine. Two modes:
///
/// <list type="bullet">
///   <item>A <c>Bridge:AccessToken</c> is configured - the request must present it as
///     <c>Authorization: Bearer &lt;token&gt;</c>. This is what makes non-loopback binding safe.</item>
///   <item>No token - only loopback callers are served. A default install over
///     <c>adb reverse</c> keeps working, and binding to a LAN address is inert until a token is set.</item>
/// </list>
/// </summary>
public sealed class BridgeAccessMiddleware
{
    private const string BearerPrefix = "Bearer ";

    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<BridgeOptions> _options;
    private readonly ILogger<BridgeAccessMiddleware> _logger;

    public BridgeAccessMiddleware(
        RequestDelegate next,
        IOptionsMonitor<BridgeOptions> options,
        ILogger<BridgeAccessMiddleware> logger)
    {
        _next = next;
        _options = options;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var configuredToken = _options.CurrentValue.AccessToken;
        var remoteIp = context.Connection.RemoteIpAddress;

        if (string.IsNullOrWhiteSpace(configuredToken))
        {
            if (IsLoopback(remoteIp))
            {
                await _next(context);
                return;
            }

            _logger.LogWarning(
                "Rejected a non-loopback {Method} {Path} from {RemoteIp}: no Bridge:AccessToken is configured.",
                context.Request.Method,
                context.Request.Path,
                remoteIp);
            await WriteUnauthorizedAsync(
                context,
                "This bridge only serves loopback callers until Bridge:AccessToken is configured in appsettings.Local.json.");
            return;
        }

        if (!TryReadBearerToken(context, out var presentedToken) ||
            !FixedTimeEquals(presentedToken, configuredToken))
        {
            _logger.LogWarning(
                "Rejected {Method} {Path} from {RemoteIp}: missing or invalid bearer token.",
                context.Request.Method,
                context.Request.Path,
                remoteIp);
            await WriteUnauthorizedAsync(context, "A valid 'Authorization: Bearer <token>' header is required.");
            return;
        }

        await _next(context);
    }

    private static bool IsLoopback(IPAddress? address) =>
        address is not null &&
        (IPAddress.IsLoopback(address) ||
         (address.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(address.MapToIPv4())));

    private static bool TryReadBearerToken(HttpContext context, out string token)
    {
        token = string.Empty;
        var header = context.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header) ||
            !header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        token = header[BearerPrefix.Length..].Trim();
        return token.Length > 0;
    }

    /// <summary>Length-independent comparison so the response time does not leak the token.</summary>
    private static bool FixedTimeEquals(string presented, string configured)
    {
        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        var configuredHash = SHA256.HashData(Encoding.UTF8.GetBytes(configured));
        return CryptographicOperations.FixedTimeEquals(presentedHash, configuredHash);
    }

    private static Task WriteUnauthorizedAsync(HttpContext context, string detail)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return context.Response.WriteAsJsonAsync(new { error = "unauthorized", detail });
    }
}
