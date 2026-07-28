using System.Text.Json;
using HowsItGoing.Contracts;
using Microsoft.Extensions.Options;

namespace HowsItGoing.Bridge.Push;

public sealed class StoredPushSubscription
{
    public string Endpoint { get; set; } = string.Empty;

    public string P256dh { get; set; } = string.Empty;

    public string Auth { get; set; } = string.Empty;

    public string? Label { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? LastPushedAt { get; set; }

    public WebPushPreferencesDto Preferences { get; set; } = new();
}

/// <summary>
/// Browser push subscriptions plus the VAPID identity, persisted next to the feed's own state so a
/// bridge restart does not silently stop notifying every browser that had opted in.
/// </summary>
public sealed class WebPushSubscriptionStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _statePath;
    private readonly WebPushOptions _options;
    private PersistentPushState _state;

    public WebPushSubscriptionStore(IOptions<WebPushOptions> options)
    {
        _options = options.Value;

        var baseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HowsItGoing",
            "Bridge");

        Directory.CreateDirectory(baseDirectory);
        _statePath = Path.Combine(baseDirectory, "push-subscriptions.json");
        _state = LoadState();
    }

    /// <summary>
    /// The VAPID pair the browser must subscribe against. Configured keys win; otherwise a generated
    /// pair is persisted once and reused, because rotating it invalidates every live subscription.
    /// </summary>
    public async Task<VapidKeyPair> GetVapidKeysAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.PublicKey) && !string.IsNullOrWhiteSpace(_options.PrivateKey))
        {
            return new VapidKeyPair(_options.PublicKey.Trim(), _options.PrivateKey.Trim());
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_state.VapidPublicKey is { Length: > 0 } publicKey && _state.VapidPrivateKey is { Length: > 0 } privateKey)
            {
                return new VapidKeyPair(publicKey, privateKey);
            }

            var generated = WebPushCrypto.GenerateVapidKeys();
            _state.VapidPublicKey = generated.PublicKey;
            _state.VapidPrivateKey = generated.PrivateKey;
            await PersistLockedAsync(cancellationToken);
            return generated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StoredPushSubscription> UpsertAsync(WebPushSubscribeRequest request, CancellationToken cancellationToken)
    {
        // Validate the keys here rather than at send time: a malformed subscription should fail the
        // request the browser can still see, not a background dispatch nobody is watching.
        WebPushCrypto.DecodeKey(request.Keys.P256dh, WebPushCrypto.PublicKeyLength, "p256dh");
        WebPushCrypto.DecodeKey(request.Keys.Auth, WebPushCrypto.AuthSecretLength, "auth");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existing = _state.Subscriptions.FirstOrDefault(x =>
                string.Equals(x.Endpoint, request.Endpoint, StringComparison.Ordinal));

            if (existing is null)
            {
                existing = new StoredPushSubscription { Endpoint = request.Endpoint };
                _state.Subscriptions.Add(existing);
            }

            existing.P256dh = request.Keys.P256dh;
            existing.Auth = request.Keys.Auth;
            existing.Label = request.Label;
            existing.ExpiresAt = request.ExpiresAt;
            existing.Preferences = request.Preferences ?? existing.Preferences;
            existing.UpdatedAt = DateTimeOffset.UtcNow;

            await PersistLockedAsync(cancellationToken);
            return existing;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RemoveAsync(string endpoint, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var removed = _state.Subscriptions.RemoveAll(x =>
                string.Equals(x.Endpoint, endpoint, StringComparison.Ordinal)) > 0;

            if (removed)
            {
                await PersistLockedAsync(cancellationToken);
            }

            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StoredPushSubscription?> FindAsync(string endpoint, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _state.Subscriptions.FirstOrDefault(x =>
                string.Equals(x.Endpoint, endpoint, StringComparison.Ordinal));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A snapshot, so a dispatch pass never holds the gate while talking to a push service.</summary>
    public async Task<IReadOnlyList<StoredPushSubscription>> GetAllAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _state.Subscriptions.ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkPushedAsync(string endpoint, DateTimeOffset pushedAt, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var subscription = _state.Subscriptions.FirstOrDefault(x =>
                string.Equals(x.Endpoint, endpoint, StringComparison.Ordinal));
            if (subscription is null)
            {
                return;
            }

            subscription.LastPushedAt = pushedAt;
            await PersistLockedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private PersistentPushState LoadState()
    {
        if (!File.Exists(_statePath))
        {
            return new PersistentPushState();
        }

        try
        {
            return JsonSerializer.Deserialize<PersistentPushState>(File.ReadAllText(_statePath)) ?? new PersistentPushState();
        }
        catch
        {
            return new PersistentPushState();
        }
    }

    private async Task PersistLockedAsync(CancellationToken cancellationToken)
    {
        await using var stream = File.Create(_statePath);
        await JsonSerializer.SerializeAsync(stream, _state, cancellationToken: cancellationToken);
    }

    private sealed class PersistentPushState
    {
        public string? VapidPublicKey { get; set; }

        public string? VapidPrivateKey { get; set; }

        public List<StoredPushSubscription> Subscriptions { get; set; } = [];
    }
}
