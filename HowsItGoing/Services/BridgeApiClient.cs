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
        using var client = CreateClient(settings.BridgeBaseUrl);
        using var response = await client.PostAsJsonAsync("/api/agent/start-run", request, SerializerOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<StartCodexRunResponse>(stream, SerializerOptions, cancellationToken);
    }

    private async Task<T?> GetAsync<T>(string relativeUrl, CancellationToken cancellationToken)
    {
        var settings = await _settingsStore.LoadAsync(cancellationToken);
        using var client = CreateClient(settings.BridgeBaseUrl);
        using var response = await client.GetAsync(relativeUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, SerializerOptions, cancellationToken);
    }

    private static HttpClient CreateClient(string baseUrl) =>
        new()
        {
            BaseAddress = new Uri(NormalizeBaseUrl(baseUrl)),
            Timeout = TimeSpan.FromSeconds(15)
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
            return "http://10.0.2.2:5217/";
        }

        return baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : baseUrl + "/";
    }
}
