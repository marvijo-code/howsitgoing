using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using HowsItGoing.Contracts;
using Microsoft.Extensions.Options;

namespace HowsItGoing.Bridge.Push;

public enum WebPushDeliveryStatus
{
    Delivered,

    /// <summary>The push service says the subscription is gone - the caller should drop it.</summary>
    Gone,

    Failed
}

public sealed record WebPushDeliveryResult(WebPushDeliveryStatus Status, string? Error = null);

/// <summary>Encrypts one feed notification and hands it to the browser's push service.</summary>
public sealed class WebPushSender
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WebPushSubscriptionStore _store;
    private readonly IOptionsMonitor<WebPushOptions> _options;
    private readonly ILogger<WebPushSender> _logger;

    public WebPushSender(
        IHttpClientFactory httpClientFactory,
        WebPushSubscriptionStore store,
        IOptionsMonitor<WebPushOptions> options,
        ILogger<WebPushSender> logger)
    {
        _httpClientFactory = httpClientFactory;
        _store = store;
        _options = options;
        _logger = logger;
    }

    public async Task<WebPushDeliveryResult> SendAsync(
        StoredPushSubscription subscription,
        BridgeNotificationDto notification,
        CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            return new WebPushDeliveryResult(WebPushDeliveryStatus.Failed, "Web push is disabled by configuration.");
        }

        if (!Uri.TryCreate(subscription.Endpoint, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttps && !endpoint.IsLoopback))
        {
            return new WebPushDeliveryResult(WebPushDeliveryStatus.Gone, "The subscription endpoint is not a usable HTTPS URL.");
        }

        try
        {
            var keys = await _store.GetVapidKeysAsync(cancellationToken);
            var payload = BuildPayload(notification, options.MaxPayloadBytes);

            var salt = RandomNumberGenerator.GetBytes(WebPushCrypto.SaltLength);
            // A fresh application-server key per message, as RFC 8291 requires.
            using var serverKey = WebPushCrypto.CreateEphemeralServerKey();

            var body = WebPushCrypto.Encrypt(
                payload,
                WebPushCrypto.DecodeKey(subscription.P256dh, WebPushCrypto.PublicKeyLength, "p256dh"),
                WebPushCrypto.DecodeKey(subscription.Auth, WebPushCrypto.AuthSecretLength, "auth"),
                salt,
                serverKey);

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new ByteArrayContent(body)
            };

            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.ContentEncoding.Add("aes128gcm");
            request.Headers.TryAddWithoutValidation(
                "Authorization",
                WebPushCrypto.CreateVapidAuthorization(
                    endpoint,
                    options.Subject,
                    keys,
                    DateTimeOffset.UtcNow.AddHours(12)));
            request.Headers.TryAddWithoutValidation("TTL", options.TimeToLiveSeconds.ToString());
            request.Headers.TryAddWithoutValidation("Urgency", "normal");

            var client = _httpClientFactory.CreateClient(nameof(WebPushSender));
            client.Timeout = TimeSpan.FromSeconds(20);

            using var response = await client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return new WebPushDeliveryResult(WebPushDeliveryStatus.Delivered);
            }

            // 404/410 is the push service telling us the browser revoked or replaced the subscription.
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return new WebPushDeliveryResult(WebPushDeliveryStatus.Gone, $"The push service returned {(int)response.StatusCode}.");
            }

            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            return new WebPushDeliveryResult(
                WebPushDeliveryStatus.Failed,
                $"The push service returned {(int)response.StatusCode}: {Truncate(detail, 200)}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Web push delivery to {Endpoint} failed.", Describe(subscription.Endpoint));
            return new WebPushDeliveryResult(WebPushDeliveryStatus.Failed, ex.Message);
        }
    }

    /// <summary>
    /// The service worker reads exactly these fields. The message is trimmed to keep the encrypted
    /// body inside one record - push services reject oversized bodies outright.
    /// </summary>
    private static byte[] BuildPayload(BridgeNotificationDto notification, int maxPayloadBytes)
    {
        var budget = Math.Max(200, maxPayloadBytes);

        for (var messageLimit = 1200; ; messageLimit /= 2)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                id = notification.Id,
                kind = (int)notification.Kind,
                kindName = notification.Kind.ToString(),
                title = Truncate(notification.Title, 120),
                body = Truncate(notification.Message, messageLimit),
                url = notification.RelatedUrl,
                repository = notification.RelatedRepository,
                sessionId = notification.RelatedSessionId,
                occurredAt = notification.OccurredAt
            });

            if (payload.Length <= budget || messageLimit <= 32)
            {
                return payload;
            }
        }
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength), "…");
    }

    /// <summary>
    /// Endpoints embed a per-browser secret, so logs get the origin and a short hash of the path
    /// instead of the value itself.
    /// </summary>
    internal static string Describe(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return "<invalid endpoint>";
        }

        var digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(endpoint));
        return $"{uri.Host}/…{Base64Url.EncodeToString(digest.AsSpan(0, 6))}";
    }
}
