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

    public BridgeApiClient(AppSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public async Task<IReadOnlyList<CodexSessionSummaryDto>> GetSessionsAsync(
        string? query,
        string? status,
        string? source,
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
                ["includeArchived"] = includeArchived.ToString().ToLowerInvariant()
            });

        return await GetAsync<IReadOnlyList<CodexSessionSummaryDto>>(endpoint, cancellationToken) ?? [];
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

        return await GetAsync<IReadOnlyList<BridgeNotificationDto>>(endpoint, cancellationToken) ?? [];
    }

    public async Task<RepositoryStatusDto?> GetRepositoryStatusAsync(CancellationToken cancellationToken = default) =>
        await GetAsync<RepositoryStatusDto>("/api/repository/status", cancellationToken);

    public async Task<BridgeSettingsDto?> GetBridgeSettingsAsync(CancellationToken cancellationToken = default) =>
        await GetAsync<BridgeSettingsDto>("/api/settings", cancellationToken);

    public async Task<UpdateInfoDto?> GetUpdateInfoAsync(int? currentVersionCode, string? currentVersion, CancellationToken cancellationToken = default)
    {
        var endpoint = BuildEndpoint(
            "/api/update",
            new Dictionary<string, string?>
            {
                ["currentVersionCode"] = currentVersionCode?.ToString(),
                ["currentVersion"] = currentVersion
            });

        return await GetAsync<UpdateInfoDto>(endpoint, cancellationToken);
    }

    public async Task<StartCodexRunResponse?> StartAgentRunAsync(StartCodexRunRequest request, CancellationToken cancellationToken = default)
    {
        var settings = await _settingsStore.LoadAsync(cancellationToken);
        var baseUrl = await ResolveBaseUrlAsync(settings, cancellationToken);
        using var client = CreateClient(baseUrl);
        using var response = await SendWithRetryAsync(
            token => client.PostAsJsonAsync("/api/agent/start-run", request, SerializerOptions, token),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<StartCodexRunResponse>(stream, SerializerOptions, cancellationToken);
    }

    private async Task<T?> GetAsync<T>(string relativeUrl, CancellationToken cancellationToken)
    {
        var settings = await _settingsStore.LoadAsync(cancellationToken);
        var baseUrl = await ResolveBaseUrlAsync(settings, cancellationToken);
        using var client = CreateClient(baseUrl);
        using var response = await SendWithRetryAsync(
            token => client.GetAsync(relativeUrl, token),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, SerializerOptions, cancellationToken);
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

    private static HttpClient CreateClient(string baseUrl, TimeSpan? timeout = null) =>
        new()
        {
            BaseAddress = new Uri(NormalizeBaseUrl(baseUrl)),
            Timeout = timeout ?? TimeSpan.FromSeconds(30)
        };

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
}
