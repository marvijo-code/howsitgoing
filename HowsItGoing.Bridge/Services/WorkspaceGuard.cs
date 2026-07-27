using Microsoft.Extensions.Configuration;

namespace HowsItGoing.Bridge.Services;

/// <summary>
/// Confirms a requested workspace is one the bridge is willing to run an agent in.
///
/// Agents are launched with approval prompts disabled, so the workspace root is the blast radius.
/// Without this check any existing directory - <c>C:\</c>, the user profile - could be handed to an
/// auto-approving agent by a single request.
/// </summary>
internal static class WorkspaceGuard
{
    internal static void EnsureAllowed(string path, IConfiguration configuration, IHostEnvironment environment)
    {
        var allowedRoots = ResolveAllowedRoots(configuration, environment);
        if (RequestGuards.IsWithinAllowedRoots(path, allowedRoots))
        {
            return;
        }

        throw new UnauthorizedAccessException(
            $"'{path}' is outside the allowed repository roots. Add it to Bridge:AllowedRepositoryRoots " +
            $"to permit it. Currently allowed: {(allowedRoots.Count == 0 ? "(none)" : string.Join(", ", allowedRoots))}.");
    }

    internal static IReadOnlyList<string> ResolveAllowedRoots(IConfiguration configuration, IHostEnvironment environment)
    {
        var bridgeOptions = configuration.GetSection(BridgeOptions.SectionName).Get<BridgeOptions>() ?? new BridgeOptions();
        var gitHubOptions = configuration.GetSection(GitHubMonitorOptions.SectionName).Get<GitHubMonitorOptions>() ?? new GitHubMonitorOptions();
        var monitoredRepositoryPath = GitHubMonitorOptions.ResolveMonitoredRepositoryPath(
            environment.ContentRootPath,
            gitHubOptions.MonitoredRepositoryPath);

        return RequestGuards.ResolveAllowedRoots(bridgeOptions, monitoredRepositoryPath);
    }
}
