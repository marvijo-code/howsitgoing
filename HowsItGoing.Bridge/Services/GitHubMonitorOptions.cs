namespace HowsItGoing.Bridge.Services;

public sealed class GitHubMonitorOptions
{
    public const string SectionName = "GitHub";

    public int PollSeconds { get; set; } = 60;

    public string MonitoredRepositoryPath { get; set; } = "..";

    public static string ResolveMonitoredRepositoryPath(string contentRootPath, string? configuredPath)
    {
        var configured = string.IsNullOrWhiteSpace(configuredPath) ? ".." : configuredPath;
        var expanded = Environment.ExpandEnvironmentVariables(configured);
        return Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(contentRootPath, expanded));
    }

    public static string? ResolveRemoteUrl(string repositoryPath)
    {
        var configPath = Path.Combine(repositoryPath, ".git", "config");
        if (!File.Exists(configPath))
        {
            return null;
        }

        var inOrigin = false;
        foreach (var rawLine in File.ReadLines(configPath))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("[", StringComparison.Ordinal))
            {
                inOrigin = line.Equals("[remote \"origin\"]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inOrigin && line.StartsWith("url", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
                return parts.Length == 2 ? parts[1] : null;
            }
        }

        return null;
    }

    public static (string Owner, string Name)? TryExtractRepositorySlug(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return null;
        }

        var trimmed = remoteUrl.Trim();
        var marker = trimmed.Contains("github.com:", StringComparison.OrdinalIgnoreCase) ? "github.com:" : "github.com/";
        var markerIndex = trimmed.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        var slug = trimmed[(markerIndex + marker.Length)..];
        if (slug.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            slug = slug[..^4];
        }

        var parts = slug.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 2 ? (parts[0], parts[1]) : null;
    }
}
