namespace HowsItGoing.Bridge.Push;

public sealed class WebPushOptions
{
    public const string SectionName = "WebPush";

    /// <summary>
    /// Set false to keep the endpoints reachable but refuse every send - useful when a machine should
    /// serve the feed without ever reaching out to a push service.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The VAPID <c>sub</c> claim. Push services want a way to contact whoever is sending, so it must
    /// be a <c>mailto:</c> or <c>https:</c> URI.
    /// </summary>
    public string Subject { get; set; } = "mailto:howsitgoing@localhost";

    /// <summary>
    /// Base64url VAPID keys. Left empty the bridge generates a pair on first use and persists it, so a
    /// default install needs no configuration and keeps the same identity across restarts (changing it
    /// would silently invalidate every existing browser subscription).
    /// </summary>
    public string? PublicKey { get; set; }

    public string? PrivateKey { get; set; }

    /// <summary>How long the push service should hold an undelivered message for.</summary>
    public int TimeToLiveSeconds { get; set; } = 3600;

    /// <summary>
    /// Cap on the encrypted payload. RFC 8291 guarantees 4096 bytes; staying under it keeps the
    /// message inside a single record, which is all <see cref="WebPushCrypto"/> emits.
    /// </summary>
    public int MaxPayloadBytes { get; set; } = 3000;
}
