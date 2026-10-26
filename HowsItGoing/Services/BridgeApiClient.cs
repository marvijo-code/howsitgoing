using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using HowsItGoing.Contracts;

namespace HowsItGoing.Services;

public sealed class BridgeApiClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly string[] AndroidBridgeCandidates = ["http://127.0.0.1:5217/", "http://10.0.2.2:5217/", "http://localhost:5217/"];
    private static readonly string[] DesktopBridgeCandidates = ["http://127.0.0.1:5217/", "http://localhost:5217/"];

    private readonly AppSettingsStore _settingsStore;
    private readonly SharedBridgeStore _sharedStore;

    /// <summary>
    /// One HttpClient per bridge address instead of one per request. Building a client per call
    /// meant a fresh socket handshake for every endpoint on every 30s refresh.
    /// </summary>
    private static readonly ConcurrentDictionary<string, HttpClient> Clients = new(StringComparer.Ordinal);

    private static readonly TimeSpan ResolvedBaseUrlLifetime = TimeSpan.FromMinutes(5);

    private string? _resolvedBaseUrl;
    private string? _resolvedConfiguredBaseUrl;
    private DateTimeOffset _resolvedAt;

    public BridgeApiClient(AppSettingsStore settingsStore, SharedBridgeStore sharedStore)
    {
        _settingsStore = settingsStore;
        _sharedStore = sharedStore;
    }

    public async Task<IReadOnlyList<CodexSessionSummaryDto>> GetSessionsAsync(
        string? query,
        string? status,
        string? source,
        string? agent,
        bool includeArchived,
        CancellationToken cancellationToken = default)
    {
        var page = await GetSessionsPageAsync(
            query,
            status,
            source,
            agent,
            includeArchived,
            offset: 0,
            limit: SessionPaging.MaxPageSize,
            cancellationToken);
        return page?.Items ?? [];
    }

    /// <summary>
    /// Fetches one page of the feed. The bridge is asked for a page, and the shared MySQL mirror is
    /// asked for the same page, so the phone never downloads a 13k-row list to show 30 cards.
    /// </summary>
    public async Task<SessionPageDto?> GetSessionsPageAsync(
        string? query,
        string? status,
        string? source,
        string? agent,
        bool includeArchived,
        int offset,
        int limit,
        CancellationToken cancellationToken = default,
        string? cursor = null)
    {
        var safeOffset = SessionPaging.ClampOffset(offset);
        var safeLimit = SessionPaging.ClampPageSize(limit);
        var endpoint = BuildEndpoint(
            "/api/sessions",
            new Dictionary<string, string?>
            {
                ["query"] = query,
                ["status"] = status,
                ["source"] = source,
                ["agent"] = agent,
                ["includeArchived"] = includeArchived.ToString().ToLowerInvariant(),
                ["offset"] = safeOffset.ToString(),
                ["limit"] = safeLimit.ToString(),
                ["cursor"] = cursor
            });

        try
        {
            return await GetDirectAsync<SessionPageDto>(endpoint, cancellationToken);
        }
        catch (Exception ex) when (IsBridgeUnreachable(ex, cancellationToken) && _sharedStore.IsConfigured)
        {
            return await _sharedStore.GetSessionsPageAsync(
                query,
                status,
                source,
                agent,
                includeArchived,
                safeOffset,
                safeLimit,
                cancellationToken,
                cursor);
        }
    }

    public async Task<SessionFollowUpResponse?> SendFollowUpAsync(SessionFollowUpRequest request, CancellationToken cancellationToken = default)
    {
        var settings = await _settingsStore.LoadAsync(cancellationToken);
        var baseUrl = await ResolveBaseUrlAsync(settings, cancellationToken);
        var client = GetClient(baseUrl, settings.BridgeAccessToken);
        using var response = await SendWithRetryAsync(
            token => client.PostAsJsonAsync("/api/sessions/follow-up", request, SerializerOptions, token),
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(detail)
                    ? $"Bridge returned {(int)response.StatusCode} {response.ReasonPhrase}."
                    : detail.Trim());
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<SessionFollowUpResponse>(stream, SerializerOptions, cancellationToken);
    }

    public async Task<IReadOnlyList<BridgeNotificationDto>> GetNotificationsAsync(DateTimeOffset? since, int limit, CancellationToken cancellationToken = default)
    {
        var endpoint = BuildEndpoint(
            "/api/notifications",
            new Dictionary<string, string?>
            {
                ["since"] = since?.ToString("O"),
                ["limit"] = limit.ToString()
            });

        return await ExecuteWithSharedFallbackAsync(
                   directOperation: async token => await GetDirectAsync<IReadOnlyList<BridgeNotificationDto>>(endpoint, token) ?? [],
                   sharedOperation: token => _sharedStore.GetNotificationsAsync(since, limit, token),
                   cancellationToken)
               ;
    }

    public async Task<RepositoryStatusDto?> GetRepositoryStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await GetDirectAsync<RepositoryStatusDto>("/api/repository/status", cancellationToken);
        }
        catch (Exception ex) when (IsBridgeUnreachable(ex, cancellationToken) && _sharedStore.IsConfigured)
        {
            return null;
        }
    }

    public async Task<IssueBoardDto?> GetIssueBoardAsync(string? repositories, string? state, CancellationToken cancellationToken = default)
    {
        var endpoint = BuildEndpoint(
            "/api/issues",
            new Dictionary<string, string?>
            {
                ["repo"] = repositories,
                ["state"] = state
            });

        try
        {
            return await GetDirectAsync<IssueBoardDto>(endpoint, cancellationToken);
        }
        catch (Exception ex) when (IsBridgeUnreachable(ex, cancellationToken))
        {
            return null;
        }
    }

    public async Task<BridgeSettingsDto?> GetBridgeSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await GetDirectAsync<BridgeSettingsDto>("/api/settings", cancellationToken);
        }
        catch (Exception ex) when (IsBridgeUnreachable(ex, cancellationToken) && _sharedStore.IsConfigured)
        {
            return null;
        }
    }

    public async Task<UpdateInfoDto?> GetUpdateInfoAsync(int? currentVersionCode, string? currentVersion, CancellationToken cancellationToken = default)
    {
        var endpoint = BuildEndpoint(
            "/api/update",
            new Dictionary<string, string?>
            {
                ["currentVersionCode"] = currentVersionCode?.ToString(),
                ["currentVersion"] = currentVersion
            });

        try
        {
            return await GetDirectAsync<UpdateInfoDto>(endpoint, cancellationToken);
        }
        catch (Exception ex) when (IsBridgeUnreachable(ex, cancellationToken) && _sharedStore.IsConfigured)
        {
            return null;
        }
    }

    public async Task<StartCodexRunResponse?> StartAgentRunAsync(StartCodexRunRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await _settingsStore.LoadAsync(cancellationToken);
            var baseUrl = await ResolveBaseUrlAsync(settings, cancellationToken);
            var client = GetClient(baseUrl, settings.BridgeAccessToken);
            using var response = await SendWithRetryAsync(
                token => client.PostAsJsonAsync("/api/agent/start-run", request, SerializerOptions, token),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(detail)
                        ? $"Bridge returned {(int)response.StatusCode} {response.ReasonPhrase}."
                        : detail.Trim());
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<StartCodexRunResponse>(stream, SerializerOptions, cancellationToken);
        }
        catch (Exception ex) when (IsBridgeUnreachable(ex, cancellationToken) && _sharedStore.IsConfigured)
        {
            var requestedBy = OperatingSystem.IsAndroid() ? "android-app" : Environment.MachineName;
            return await _sharedStore.QueueStartRunAndWaitAsync(request, requestedBy, cancellationToken);
        }
    }

    /// <summary>
    /// The VAPID public key the browser needs before it can subscribe. No shared-store fallback: push
    /// registration is meaningless without a bridge to send from.
    /// </summary>
    public async Task<WebPushConfigDto?> GetPushConfigAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await GetDirectAsync<WebPushConfigDto>("/api/push/config", cancellationToken);
        }
        catch (Exception ex) when (IsBridgeUnreachable(ex, cancellationToken))
        {
            return null;
        }
    }

    public async Task<WebPushSubscriptionStatusDto?> GetPushSubscriptionAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        var url = BuildEndpoint("/api/push/subscription", new Dictionary<string, string?> { ["endpoint"] = endpoint });

        try
        {
            return await GetDirectAsync<WebPushSubscriptionStatusDto>(url, cancellationToken);
        }
        catch (Exception ex) when (IsBridgeUnreachable(ex, cancellationToken))
        {
            return null;
        }
    }

    /// <summary>Registers a subscription, or updates the preferences of one already registered.</summary>
    public Task<WebPushSubscriptionStatusDto?> SavePushSubscriptionAsync(WebPushSubscribeRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<WebPushSubscribeRequest, WebPushSubscriptionStatusDto>("/api/push/subscribe", request, cancellationToken);

    public Task<WebPushSubscriptionStatusDto?> RemovePushSubscriptionAsync(string endpoint, CancellationToken cancellationToken = default) =>
        PostAsync<WebPushUnsubscribeRequest, WebPushSubscriptionStatusDto>("/api/push/unsubscribe", new WebPushUnsubscribeRequest(endpoint), cancellationToken);

    public Task<WebPushSendResultDto?> SendTestPushAsync(string endpoint, CancellationToken cancellationToken = default) =>
        PostAsync<WebPushUnsubscribeRequest, WebPushSendResultDto>("/api/push/test", new WebPushUnsubscribeRequest(endpoint), cancellationToken);

    private async Task<TResponse?> PostAsync<TRequest, TResponse>(string relativeUrl, TRequest request, CancellationToken cancellationToken)
    {
        var settings = await _settingsStore.LoadAsync(cancellationToken);
        var baseUrl = await ResolveBaseUrlAsync(settings, cancellationToken);
        var client = GetClient(baseUrl, settings.BridgeAccessToken);
        using var response = await SendWithRetryAsync(
            token => client.PostAsJsonAsync(relativeUrl, request, SerializerOptions, token),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(detail)
                    ? $"Bridge returned {(int)response.StatusCode} {response.ReasonPhrase}."
                    : detail.Trim());
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<TResponse>(stream, SerializerOptions, cancellationToken);
    }

    private async Task<T?> GetDirectAsync<T>(string relativeUrl, CancellationToken cancellationToken)
    {
        var settings = await _settingsStore.LoadAsync(cancellationToken);
        var baseUrl = await ResolveBaseUrlAsync(settings, cancellationToken);
        var client = GetClient(baseUrl, settings.BridgeAccessToken);
        using var response = await SendWithRetryAsync(
            token => client.GetAsync(relativeUrl, token),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, SerializerOptions, cancellationToken);
    }

    private async Task<T> ExecuteWithSharedFallbackAsync<T>(
        Func<CancellationToken, Task<T>> directOperation,
        Func<CancellationToken, Task<T>> sharedOperation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await directOperation(cancellationToken);
        }
        catch (Exception ex) when (IsBridgeUnreachable(ex, cancellationToken) && _sharedStore.IsConfigured)
        {
            return await sharedOperation(cancellationToken);
        }
    }

    private async Task<string> ResolveBaseUrlAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var configuredBaseUrl = NormalizeBaseUrl(settings.BridgeBaseUrl);

        // Probing /healthz on every call cost an extra round trip per API request, which on a phone
        // over adb reverse is most of the refresh. A resolved URL is reused until the configured
        // value changes or the short cache window expires.
        if (_resolvedBaseUrl is not null &&
            string.Equals(_resolvedConfiguredBaseUrl, configuredBaseUrl, StringComparison.OrdinalIgnoreCase) &&
            DateTimeOffset.UtcNow - _resolvedAt < ResolvedBaseUrlLifetime)
        {
            return _resolvedBaseUrl;
        }

        var candidates = BuildCandidateBaseUrls(configuredBaseUrl);

        if (candidates.Count == 1)
        {
            return Remember(configuredBaseUrl, configuredBaseUrl);
        }

        foreach (var candidate in candidates)
        {
            if (!await IsReachableAsync(candidate, cancellationToken))
            {
                continue;
            }

            if (!string.Equals(candidate, configuredBaseUrl, StringComparison.OrdinalIgnoreCase))
            {
                await _settingsStore.SaveAsync(settings with { BridgeBaseUrl = candidate.TrimEnd('/') }, cancellationToken);
            }

            return Remember(configuredBaseUrl, candidate);
        }

        return configuredBaseUrl;
    }

    private string Remember(string configuredBaseUrl, string resolvedBaseUrl)
    {
        _resolvedConfiguredBaseUrl = configuredBaseUrl;
        _resolvedBaseUrl = resolvedBaseUrl;
        _resolvedAt = DateTimeOffset.UtcNow;
        return resolvedBaseUrl;
    }

    private static IReadOnlyList<string> BuildCandidateBaseUrls(string configuredBaseUrl)
    {
        if (!ShouldTryFallbacks(configuredBaseUrl))
        {
            return [configuredBaseUrl];
        }

        var candidates = new List<string> { configuredBaseUrl };
        foreach (var candidate in GetFallbackBaseUrls())
        {
            if (!candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    private static bool ShouldTryFallbacks(string configuredBaseUrl)
    {
        if (!Uri.TryCreate(configuredBaseUrl, UriKind.Absolute, out var uri))
        {
            return true;
        }

        return uri.Host.Equals("10.0.2.2", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> GetFallbackBaseUrls() =>
        OperatingSystem.IsAndroid() ? AndroidBridgeCandidates : DesktopBridgeCandidates;

    private static async Task<bool> IsReachableAsync(string baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient(baseUrl, TimeSpan.FromSeconds(2));
            using var response = await client.GetAsync("/healthz", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation(cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            return await operation(cancellationToken);
        }
    }

    private static HttpClient CreateClient(string baseUrl, TimeSpan? timeout = null, string? accessToken = null)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(NormalizeBaseUrl(baseUrl)),
            Timeout = timeout ?? TimeSpan.FromSeconds(30)
        };

        // The bridge requires this for every /api call once Bridge:AccessToken is set, which it
        // must be whenever the bridge is reached over anything but loopback.
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken.Trim());
        }

        return client;
    }

    /// <summary>
    /// Returns a long-lived client for this address/token pair. The token is only used to build a
    /// cache key; nothing is logged or persisted with it.
    /// </summary>
    private static HttpClient GetClient(string baseUrl, string? accessToken)
    {
        var normalized = NormalizeBaseUrl(baseUrl);
        var tokenKey = string.IsNullOrWhiteSpace(accessToken)
            ? "0"
            : accessToken.Trim().GetHashCode(StringComparison.Ordinal).ToString("x");
        var key = $"{normalized}|{tokenKey}";

        return Clients.GetOrAdd(key, _ => CreateClient(normalized, accessToken: accessToken));
    }

    private static string BuildEndpoint(string path, IReadOnlyDictionary<string, string?> query)
    {
        var parts = query
            .Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value!)}")
            .ToArray();

        return parts.Length == 0 ? path : $"{path}?{string.Join("&", parts)}";
    }

    private static string NormalizeBaseUrl(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return "http://127.0.0.1:5217/";
        }

        return baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : baseUrl + "/";
    }

    private static bool IsBridgeUnreachable(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return true;
        }

        return exception is HttpRequestException httpRequestException && httpRequestException.StatusCode is null;
    }
}
