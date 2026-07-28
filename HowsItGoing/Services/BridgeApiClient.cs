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
        var endpoint = BuildEndpoint(
            "/api/sessions",
            new Dictionary<string, string?>
            {
                ["query"] = query,
                ["status"] = status,
                ["source"] = source,
                ["agent"] = agent,
                ["includeArchived"] = includeArchived.ToString().ToLowerInvariant()
            });

        return await ExecuteWithSharedFallbackAsync(
                   directOperation: async token => await GetDirectAsync<IReadOnlyList<CodexSessionSummaryDto>>(endpoint, token) ?? [],
                   sharedOperation: token => _sharedStore.GetSessionsAsync(query, status, source, agent, includeArchived, token),
                   cancellationToken)
               ;
    }

    public async Task<SessionFollowUpResponse?> SendFollowUpAsync(SessionFollowUpRequest request, CancellationToken cancellationToken = default)
    {
        var settings = await _settingsStore.LoadAsync(cancellationToken);
        var baseUrl = await ResolveBaseUrlAsync(settings, cancellationToken);
        using var client = CreateClient(baseUrl, accessToken: settings.BridgeAccessToken);
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
            using var client = CreateClient(baseUrl, accessToken: settings.BridgeAccessToken);
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
        using var client = CreateClient(baseUrl, accessToken: settings.BridgeAccessToken);
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
        using var client = CreateClient(baseUrl, accessToken: settings.BridgeAccessToken);
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
        var candidates = BuildCandidateBaseUrls(configuredBaseUrl);

        if (candidates.Count == 1)
        {
            return configuredBaseUrl;
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

            return candidate;
        }

        return configuredBaseUrl;
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
