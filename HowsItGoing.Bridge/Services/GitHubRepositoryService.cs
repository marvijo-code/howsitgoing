using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using HowsItGoing.Bridge.State;
using HowsItGoing.Contracts;
using Microsoft.Extensions.Options;

namespace HowsItGoing.Bridge.Services;

public sealed class GitHubRepositoryService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BridgeStateStore _stateStore;
    private readonly GitHubMonitorOptions _options;
    private readonly ILogger<GitHubRepositoryService> _logger;
    private readonly string _contentRoot;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private RepositoryStatusDto _cachedStatus = new(null, null, null, false, false, null, null, null, null, null, null, null, null);

    public GitHubRepositoryService(
        IHttpClientFactory httpClientFactory,
        BridgeStateStore stateStore,
        IOptions<GitHubMonitorOptions> options,
        IHostEnvironment environment,
        ILogger<GitHubRepositoryService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _stateStore = stateStore;
        _options = options.Value;
        _logger = logger;
        _contentRoot = environment.ContentRootPath;
    }

    public Task<RepositoryStatusDto> GetStatusAsync(CancellationToken cancellationToken) => RefreshAsync(cancellationToken);

    public async Task<UpdateInfoDto> GetLatestUpdateAsync(int? currentVersionCode, string? currentVersion, CancellationToken cancellationToken)
    {
        var status = await RefreshAsync(cancellationToken);
        if (!status.IsConfigured || string.IsNullOrWhiteSpace(status.Owner) || string.IsNullOrWhiteSpace(status.Name))
        {
            return new UpdateInfoDto(false, null, null, null, null, null, null);
        }

        var token = await TryGetGitHubTokenAsync(cancellationToken);
        using var response = await SendGitHubRequestAsync($"/repos/{status.Owner}/{status.Name}/releases/latest", token, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new UpdateInfoDto(false, null, null, null, null, null, null);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagNameValue) ? tagNameValue.GetString() : null;
        DateTimeOffset? publishedAt = root.TryGetProperty("published_at", out var publishedAtValue) &&
                                      publishedAtValue.ValueKind == JsonValueKind.String &&
                                      DateTimeOffset.TryParse(publishedAtValue.GetString(), out var publishedValue)
            ? publishedValue
            : null;

        JsonElement? apkAsset = null;
        if (root.TryGetProperty("assets", out var assetsValue) && assetsValue.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsValue.EnumerateArray())
            {
                if (asset.TryGetProperty("name", out var assetNameValue) &&
                    assetNameValue.ValueKind == JsonValueKind.String &&
                    assetNameValue.GetString()!.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                {
                    apkAsset = asset;
                    break;
                }
            }
        }

        var latestVersionCode = TryParseVersionCode(tag);
        var isUpdateAvailable = latestVersionCode is not null && currentVersionCode is not null
            ? latestVersionCode > currentVersionCode
            : !string.IsNullOrWhiteSpace(tag) && !string.Equals(tag, currentVersion, StringComparison.OrdinalIgnoreCase);

        return new UpdateInfoDto(
            isUpdateAvailable,
            tag,
            tag?.TrimStart('v'),
            latestVersionCode,
            apkAsset?.TryGetProperty("name", out var assetName) == true ? assetName.GetString() : null,
            apkAsset?.TryGetProperty("browser_download_url", out var assetUrl) == true ? assetUrl.GetString() : null,
            publishedAt);
    }

    public async Task PollForNotificationsAsync(CancellationToken cancellationToken)
    {
        var status = await RefreshAsync(cancellationToken);
        if (!status.IsConfigured || string.IsNullOrWhiteSpace(status.Owner) || string.IsNullOrWhiteSpace(status.Name))
        {
            return;
        }

        var token = await TryGetGitHubTokenAsync(cancellationToken);
        using var response = await SendGitHubRequestAsync($"/repos/{status.Owner}/{status.Name}/events?per_page=20", token, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
        {
            return;
        }

        var pushEvents = document.RootElement.EnumerateArray()
            .Where(x => x.TryGetProperty("type", out var typeValue) &&
                        typeValue.ValueKind == JsonValueKind.String &&
                        typeValue.GetString() == "PushEvent")
            .Select(x => x.Clone())
            .ToList();

        if (pushEvents.Count == 0)
        {
            return;
        }

        UpdateCachedPushStatus(pushEvents[0], status);

        var lastSeenEventId = await _stateStore.GetLastGitHubEventIdAsync(cancellationToken);
        if (lastSeenEventId is null)
        {
            await _stateStore.SetLastGitHubEventIdAsync(pushEvents[0].GetProperty("id").GetString(), cancellationToken);
            return;
        }

        var freshEvents = new List<JsonElement>();
        foreach (var item in pushEvents)
        {
            var eventId = item.GetProperty("id").GetString();
            if (eventId == lastSeenEventId)
            {
                break;
            }

            freshEvents.Add(item);
        }

        if (freshEvents.Count == 0)
        {
            return;
        }

        await _stateStore.SetLastGitHubEventIdAsync(freshEvents[0].GetProperty("id").GetString(), cancellationToken);

        foreach (var item in freshEvents.AsEnumerable().Reverse())
        {
            var actor = item.TryGetProperty("actor", out var actorValue) &&
                        actorValue.TryGetProperty("login", out var loginValue) &&
                        loginValue.ValueKind == JsonValueKind.String
                ? loginValue.GetString()
                : "unknown";

            var createdAt = item.TryGetProperty("created_at", out var createdAtValue) &&
                            createdAtValue.ValueKind == JsonValueKind.String &&
                            DateTimeOffset.TryParse(createdAtValue.GetString(), out var createdAtParsed)
                ? createdAtParsed
                : DateTimeOffset.UtcNow;

            var payload = item.GetProperty("payload");
            var branch = payload.TryGetProperty("ref", out var refValue) && refValue.ValueKind == JsonValueKind.String
                ? refValue.GetString()?.Replace("refs/heads/", string.Empty, StringComparison.OrdinalIgnoreCase)
                : status.DefaultBranch;
            var headSha = payload.TryGetProperty("head", out var headValue) ? headValue.GetString() : null;
            var message = payload.TryGetProperty("commits", out var commitsValue) &&
                          commitsValue.ValueKind == JsonValueKind.Array &&
                          commitsValue.GetArrayLength() > 0 &&
                          commitsValue[0].TryGetProperty("message", out var messageValue) &&
                          messageValue.ValueKind == JsonValueKind.String
                ? messageValue.GetString()
                : "A new push was detected on GitHub.";

            await _stateStore.AddNotificationAsync(
                new BridgeNotificationDto(
                    $"github-push:{item.GetProperty("id").GetString()}",
                    BridgeNotificationKind.GitHubPush,
                    $"Push to {status.Owner}/{status.Name}",
                    $"{actor} pushed to {branch}: {message}",
                    createdAt,
                    null,
                    $"{status.Owner}/{status.Name}",
                    !string.IsNullOrWhiteSpace(headSha) ? $"https://github.com/{status.Owner}/{status.Name}/commit/{headSha}" : null),
                cancellationToken);
        }
    }

    private async Task<RepositoryStatusDto> RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            var monitoredRepoPath = GitHubMonitorOptions.ResolveMonitoredRepositoryPath(_contentRoot, _options.MonitoredRepositoryPath);
            var remoteUrl = GitHubMonitorOptions.ResolveRemoteUrl(monitoredRepoPath);
            var repository = GitHubMonitorOptions.TryExtractRepositorySlug(remoteUrl);
            if (repository is null)
            {
                _cachedStatus = new RepositoryStatusDto(null, null, null, false, false, null, null, null, null, null, null, null, null);
                return _cachedStatus;
            }

            var token = await TryGetGitHubTokenAsync(cancellationToken);
            using var response = await SendGitHubRequestAsync($"/repos/{repository.Value.Owner}/{repository.Value.Name}", token, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _cachedStatus = _cachedStatus with
                {
                    Owner = repository.Value.Owner,
                    Name = repository.Value.Name,
                    IsConfigured = true,
                    IsAuthenticated = !string.IsNullOrWhiteSpace(token)
                };
                return _cachedStatus;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var defaultBranch = root.TryGetProperty("default_branch", out var branchValue) && branchValue.ValueKind == JsonValueKind.String
                ? branchValue.GetString()
                : "main";

            var (releaseTag, releaseAssetName, releaseUrl) = await TryGetReleaseMetadataAsync(repository.Value.Owner, repository.Value.Name, token, cancellationToken);

            _cachedStatus = _cachedStatus with
            {
                Owner = repository.Value.Owner,
                Name = repository.Value.Name,
                DefaultBranch = defaultBranch,
                IsConfigured = true,
                IsAuthenticated = !string.IsNullOrWhiteSpace(token),
                LastReleaseTag = releaseTag,
                LastReleaseAssetName = releaseAssetName,
                LastReleaseUrl = releaseUrl
            };

            return _cachedStatus;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<(string? Tag, string? AssetName, string? Url)> TryGetReleaseMetadataAsync(string owner, string name, string? token, CancellationToken cancellationToken)
    {
        try
        {
            using var releaseResponse = await SendGitHubRequestAsync($"/repos/{owner}/{name}/releases/latest", token, cancellationToken);
            if (!releaseResponse.IsSuccessStatusCode)
            {
                return (null, null, null);
            }

            await using var releaseStream = await releaseResponse.Content.ReadAsStreamAsync(cancellationToken);
            using var releaseDocument = await JsonDocument.ParseAsync(releaseStream, cancellationToken: cancellationToken);
            var releaseRoot = releaseDocument.RootElement;
            var tag = releaseRoot.TryGetProperty("tag_name", out var tagValue) ? tagValue.GetString() : null;
            var url = releaseRoot.TryGetProperty("html_url", out var urlValue) ? urlValue.GetString() : null;
            string? assetName = null;
            if (releaseRoot.TryGetProperty("assets", out var assetsValue) && assetsValue.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsValue.EnumerateArray())
                {
                    if (asset.TryGetProperty("name", out var assetNameValue) && assetNameValue.ValueKind == JsonValueKind.String)
                    {
                        assetName = assetNameValue.GetString();
                        break;
                    }
                }
            }

            return (tag, assetName, url);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to retrieve GitHub release metadata.");
            return (null, null, null);
        }
    }

    private void UpdateCachedPushStatus(JsonElement pushEvent, RepositoryStatusDto status)
    {
        var payload = pushEvent.GetProperty("payload");
        var actor = pushEvent.TryGetProperty("actor", out var actorValue) &&
                    actorValue.TryGetProperty("login", out var loginValue) &&
                    loginValue.ValueKind == JsonValueKind.String
            ? loginValue.GetString()
            : null;

        DateTimeOffset? occurredAt = pushEvent.TryGetProperty("created_at", out var createdAtValue) &&
                                     createdAtValue.ValueKind == JsonValueKind.String &&
                                     DateTimeOffset.TryParse(createdAtValue.GetString(), out var parsedAt)
            ? parsedAt
            : null;

        var branch = payload.TryGetProperty("ref", out var refValue) && refValue.ValueKind == JsonValueKind.String
            ? refValue.GetString()?.Replace("refs/heads/", string.Empty, StringComparison.OrdinalIgnoreCase)
            : status.DefaultBranch;
        var head = payload.TryGetProperty("head", out var headValue) ? headValue.GetString() : null;
        var message = payload.TryGetProperty("commits", out var commitsValue) &&
                      commitsValue.ValueKind == JsonValueKind.Array &&
                      commitsValue.GetArrayLength() > 0 &&
                      commitsValue[0].TryGetProperty("message", out var messageValue) &&
                      messageValue.ValueKind == JsonValueKind.String
            ? messageValue.GetString()
            : null;

        _cachedStatus = _cachedStatus with
        {
            LastPushActor = actor,
            LastPushAt = occurredAt,
            LastPushBranch = branch,
            LastPushSha = head,
            LastPushMessage = message
        };
    }

    private async Task<string?> TryGetGitHubTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "gh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("auth");
            startInfo.ArgumentList.Add("token");

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to retrieve GitHub token from gh.");
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendGitHubRequestAsync(string relativeUrl, string? token, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri("https://api.github.com");
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HowsItGoingBridge", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (!string.IsNullOrWhiteSpace(token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.GetAsync(relativeUrl, cancellationToken);
    }

    private static int? TryParseVersionCode(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var parts = tag.TrimStart('v').Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 0 && int.TryParse(parts[^1], out var value) ? value : null;
    }
}
