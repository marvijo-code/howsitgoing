namespace HowsItGoing.Bridge.Services;

/// <summary>
/// Turns a resolved CLI path into a launch spec. Batch shims are dereferenced to the real
/// executable so that every agent process is started with an argument vector and never through
/// a shell - see <see cref="WindowsShimResolver"/> for why.
/// </summary>
internal static class LaunchSpecFactory
{
    internal static CodexProcessLaunchSpec FromExecutablePath(string executablePath)
    {
        if (OperatingSystem.IsWindows() && WindowsShimResolver.IsBatchShim(executablePath))
        {
            var target = WindowsShimResolver.TryResolveTarget(executablePath)
                ?? throw new FileNotFoundException(
                    $"'{executablePath}' is a batch shim whose target could not be resolved. The bridge " +
                    "refuses to launch agents through cmd.exe, so point the matching " +
                    "Bridge:*ExecutablePath setting at the real executable instead.");

            return FromResolvedTarget(target);
        }

        if (OperatingSystem.IsWindows() &&
            executablePath.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            return new CodexProcessLaunchSpec(executablePath, [], CodexLaunchMode.PowerShellScript, executablePath);
        }

        return FromResolvedTarget(executablePath);
    }

    private static CodexProcessLaunchSpec FromResolvedTarget(string target)
    {
        if (target.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
        {
            var node = ResolveNodeExecutable()
                ?? throw new FileNotFoundException(
                    $"'{target}' is a Node script but no 'node' executable was found on PATH.");
            return new CodexProcessLaunchSpec(node, [target], CodexLaunchMode.Direct, target);
        }

        return new CodexProcessLaunchSpec(target, [], CodexLaunchMode.Direct, target);
    }

    private static string? ResolveNodeExecutable()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", string.Empty } : [string.Empty];
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, "node" + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
