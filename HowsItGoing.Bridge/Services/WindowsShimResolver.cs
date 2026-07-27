using System.Text.RegularExpressions;

namespace HowsItGoing.Bridge.Services;

/// <summary>
/// Resolves the real executable behind a Windows npm launcher shim (`claude.cmd`, `opencode.cmd`).
///
/// The bridge never routes an agent launch through <c>cmd.exe</c>. A batch shim would force us to
/// build a single command-line string, and <c>cmd.exe</c> does not honour the backslash escaping
/// that .NET's argument quoting produces - it toggles quote state on every <c>"</c>. Any request
/// value containing a quote could therefore close the quoting and have everything after an
/// unquoted <c>&amp;</c> run as a separate command.
///
/// npm shims are generated from a fixed template that invokes a real target relative to the shim
/// directory, so we read that target out and launch it directly with an argument vector instead.
/// </summary>
internal static partial class WindowsShimResolver
{
    [GeneratedRegex("\"(?<target>%~?dp0%?[^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex ShimTargetPattern();

    /// <summary>
    /// True when <paramref name="path"/> is a batch file that must not be executed directly.
    /// </summary>
    internal static bool IsBatchShim(string path) =>
        path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads an npm shim and returns the executable (or node script) it delegates to.
    /// Returns <see langword="null"/> when the file is not a recognisable shim, so callers can
    /// fail closed rather than fall back to a shell.
    /// </summary>
    internal static string? TryResolveTarget(string shimPath)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(shimPath);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        var shimDirectory = Path.GetDirectoryName(Path.GetFullPath(shimPath));
        if (string.IsNullOrEmpty(shimDirectory))
        {
            return null;
        }

        foreach (var line in lines)
        {
            var match = ShimTargetPattern().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var raw = match.Groups["target"].Value
                .Replace("%~dp0", shimDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                .Replace("%dp0%", shimDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

            string candidate;
            try
            {
                // GetFullPath collapses the doubled separator the shim template produces.
                candidate = Path.GetFullPath(raw);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
