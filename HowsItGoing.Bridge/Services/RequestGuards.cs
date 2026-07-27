using System.Text.RegularExpressions;

namespace HowsItGoing.Bridge.Services;

/// <summary>
/// Input validation for values that reach a launched agent process as command-line arguments or
/// as a workspace root. Everything here fails closed: an unrecognised value is rejected rather
/// than passed through.
/// </summary>
internal static partial class RequestGuards
{
    private static readonly string[] AllowedReasoningEfforts = ["minimal", "low", "medium", "high"];

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,128}$")]
    private static partial Regex SessionIdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._:/-]{1,64}$")]
    private static partial Regex ModelPattern();

    /// <summary>
    /// Session ids are agent-generated GUIDs or slugs. Constraining the shape stops a value like
    /// <c>--cd=C:\</c> from reaching a CLI's argument parser as a flag.
    /// </summary>
    internal static string ValidateSessionId(string sessionId)
    {
        var trimmed = sessionId.Trim();
        if (!SessionIdPattern().IsMatch(trimmed))
        {
            throw new ArgumentException("sessionId must be 1-128 characters of letters, digits, dot, underscore, or hyphen.");
        }

        return trimmed;
    }

    internal static string ValidateModel(string model)
    {
        var trimmed = model.Trim();
        if (!ModelPattern().IsMatch(trimmed))
        {
            throw new ArgumentException("model must be 1-64 characters of letters, digits, dot, underscore, colon, slash, or hyphen.");
        }

        return trimmed;
    }

    internal static string ValidateReasoningEffort(string reasoningEffort)
    {
        var trimmed = reasoningEffort.Trim();
        if (!AllowedReasoningEfforts.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"reasoningEffort must be one of: {string.Join(", ", AllowedReasoningEfforts)}.");
        }

        return trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// Canonicalises <paramref name="requestedPath"/> and confirms it sits inside one of
    /// <paramref name="allowedRoots"/>. Comparison happens after <see cref="Path.GetFullPath(string)"/>
    /// so <c>..</c> segments and short names cannot escape a root, and a separator boundary is
    /// required so that <c>C:\dev-secrets</c> does not match the root <c>C:\dev</c>.
    /// </summary>
    internal static bool IsWithinAllowedRoots(string requestedPath, IReadOnlyList<string> allowedRoots)
    {
        if (allowedRoots.Count == 0)
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        foreach (var root in allowedRoots)
        {
            string fullRoot;
            try
            {
                fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (fullPath.Equals(fullRoot, comparison))
            {
                return true;
            }

            if (fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The effective allowlist: explicit configuration when present, otherwise the parent of the
    /// monitored repository.
    /// </summary>
    internal static IReadOnlyList<string> ResolveAllowedRoots(BridgeOptions options, string monitoredRepositoryPath)
    {
        var configured = options.AllowedRepositoryRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Environment.ExpandEnvironmentVariables(root.Trim()))
            .ToArray();

        if (configured.Length > 0)
        {
            return configured;
        }

        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(monitoredRepositoryPath)));
        return string.IsNullOrEmpty(parent) ? [] : [parent];
    }
}
