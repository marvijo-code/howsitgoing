using System.Diagnostics;
using Microsoft.Extensions.Configuration;

namespace HowsItGoing.Bridge.Services;

internal static class AgentProcessFactory
{
    /// <summary>
    /// Environment variables injected by a parent Claude Code session; a child `claude` inheriting
    /// them reports "Not logged in", so they are stripped from launched agent processes.
    /// </summary>
    private static readonly string[] HostSessionVariables =
    [
        "ANTHROPIC_BASE_URL",
        "ANTHROPIC_MODEL"
    ];

    internal static CodexProcessLaunchSpec ResolveNpmTool(IConfiguration configuration, string configKey, string toolName)
    {
        var configuredPath = configuration[configKey];
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var expandedPath = Environment.ExpandEnvironmentVariables(configuredPath.Trim());
            if (!File.Exists(expandedPath))
            {
                throw new FileNotFoundException($"Configured {toolName} executable was not found: {expandedPath}");
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

            var npmDirectory = Path.Combine(roamingAppData, "npm");
            foreach (var candidate in new[]
                     {
                         Path.Combine(npmDirectory, toolName + ".cmd"),
                         Path.Combine(npmDirectory, toolName + ".exe"),
                         Path.Combine(npmDirectory, toolName)
                     })
            {
                if (File.Exists(candidate))
                {
                    return BuildSpec(candidate);
                }
            }
        }

        var resolved = ResolveOnPath(toolName);
        if (resolved is null)
        {
            throw new FileNotFoundException($"Could not find a {toolName} CLI executable on PATH.");
        }

        return BuildSpec(resolved);
    }

    internal static ProcessStartInfo CreateStartInfo(
        string workingDirectory,
        CodexProcessLaunchSpec launchSpec,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environmentOverrides = null)
    {
        var startInfo = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        foreach (var variable in HostSessionVariables)
        {
            startInfo.Environment.Remove(variable);
        }

        foreach (var claudeVariable in startInfo.Environment.Keys
                     .Where(key => key.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            startInfo.Environment.Remove(claudeVariable);
        }

        if (environmentOverrides is not null)
        {
            foreach (var (key, value) in environmentOverrides)
            {
                startInfo.Environment[key] = Environment.ExpandEnvironmentVariables(value);
            }
        }

        if (launchSpec.Mode == CodexLaunchMode.CmdScript)
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(BuildCommandLine(launchSpec.FileName, launchSpec.PrefixArguments.Concat(arguments)));
            return startInfo;
        }

        if (launchSpec.Mode == CodexLaunchMode.PowerShellScript)
        {
            startInfo.FileName = "powershell.exe";
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(launchSpec.FileName);
            foreach (var prefixArgument in launchSpec.PrefixArguments)
            {
                startInfo.ArgumentList.Add(prefixArgument);
            }

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            return startInfo;
        }

        startInfo.FileName = launchSpec.FileName;
        foreach (var prefixArgument in launchSpec.PrefixArguments)
        {
            startInfo.ArgumentList.Add(prefixArgument);
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static CodexProcessLaunchSpec BuildSpec(string executablePath)
    {
        if (OperatingSystem.IsWindows() &&
            (executablePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
             executablePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            return new CodexProcessLaunchSpec(executablePath, [], CodexLaunchMode.CmdScript, executablePath);
        }

        if (OperatingSystem.IsWindows() &&
            executablePath.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            return new CodexProcessLaunchSpec(executablePath, [], CodexLaunchMode.PowerShellScript, executablePath);
        }

        return new CodexProcessLaunchSpec(executablePath, [], CodexLaunchMode.Direct, executablePath);
    }

    private static string? ResolveOnPath(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var extensions = OperatingSystem.IsWindows()
            ? new[] { ".exe", ".cmd", ".bat", string.Empty }
            : new[] { string.Empty };

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, command + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    internal static string BuildCommandLine(string executablePath, IEnumerable<string> arguments)
    {
        var argumentList = arguments.ToList();
        var parts = new List<string>(argumentList.Count + 1)
        {
            QuoteForCmd(executablePath)
        };

        parts.AddRange(argumentList.Select(QuoteForCmd));
        return string.Join(' ', parts);
    }

    private static string QuoteForCmd(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        if (!value.Any(ch => char.IsWhiteSpace(ch) || ch is '"' or '^' or '&' or '|' or '<' or '>'))
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
