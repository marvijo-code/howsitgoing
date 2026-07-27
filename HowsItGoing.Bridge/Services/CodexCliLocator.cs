using Microsoft.Extensions.Configuration;

namespace HowsItGoing.Bridge.Services;

internal static class CodexCliLocator
{
    internal static CodexProcessLaunchSpec Resolve(IConfiguration configuration)
    {
        var configuredPath = configuration["Bridge:CodexExecutablePath"];
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var expandedPath = Environment.ExpandEnvironmentVariables(configuredPath.Trim());
            if (!File.Exists(expandedPath))
            {
                throw new FileNotFoundException($"Configured Codex executable was not found: {expandedPath}");
            }

            return BuildSpec(expandedPath);
        }

        if (OperatingSystem.IsWindows())
        {
            var roamingAppData = Environment.GetEnvironmentVariable("APPDATA");
            if (string.IsNullOrWhiteSpace(roamingAppData))
            {
                roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            }

            var appDataNpmDirectory = Path.Combine(roamingAppData, "npm");
            var npmCodexJs = Path.Combine(appDataNpmDirectory, "node_modules", "@openai", "codex", "bin", "codex.js");
            if (File.Exists(npmCodexJs))
            {
                var nodeExecutable = ResolveOnPath("node", preferWindowsApps: false);
                if (nodeExecutable is not null)
                {
                    return new CodexProcessLaunchSpec(nodeExecutable, [npmCodexJs], CodexLaunchMode.Direct, npmCodexJs);
                }
            }

            foreach (var candidate in new[]
                     {
                         Path.Combine(appDataNpmDirectory, "codex.cmd"),
                         Path.Combine(appDataNpmDirectory, "codex.exe"),
                         Path.Combine(appDataNpmDirectory, "codex")
                     })
            {
                if (File.Exists(candidate))
                {
                    return BuildSpec(candidate);
                }
            }
        }

        var resolved = ResolveOnPath("codex", preferWindowsApps: false) ?? ResolveOnPath("codex", preferWindowsApps: true);
        if (resolved is null)
        {
            throw new FileNotFoundException("Could not find a Codex CLI executable on PATH.");
        }

        return BuildSpec(resolved);
    }

    private static CodexProcessLaunchSpec BuildSpec(string executablePath) =>
        LaunchSpecFactory.FromExecutablePath(executablePath);

    private static string? ResolveOnPath(string command, bool preferWindowsApps)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var extensions = OperatingSystem.IsWindows()
            ? new[] { ".exe", ".cmd", ".bat", string.Empty }
            : new[] { string.Empty };
        var candidates = new List<string>();

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, command + extension);
                if (File.Exists(candidate))
                {
                    candidates.Add(candidate);
                }
            }
        }

        return candidates
            .OrderBy(candidate => ScoreCandidate(candidate, preferWindowsApps))
            .FirstOrDefault();
    }

    private static int ScoreCandidate(string candidate, bool preferWindowsApps)
    {
        var score = 0;
        if (candidate.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
        {
            score += preferWindowsApps ? 0 : 100;
        }

        if (candidate.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
            candidate.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
        }

        return score;
    }
}

internal sealed record CodexProcessLaunchSpec(
    string FileName,
    IReadOnlyList<string> PrefixArguments,
    CodexLaunchMode Mode,
    string DisplayTarget);

internal enum CodexLaunchMode
{
    Direct,
    PowerShellScript
}
