using Microsoft.Extensions.Configuration;

namespace HowsItGoing.Bridge.Services;

public sealed class BridgeOptions
{
    public const string SectionName = "Bridge";

    public int NotificationPollSeconds { get; set; } = 30;

    public int RunningThresholdSeconds { get; set; } = 150;

    public int MaxSessionsPerAgent { get; set; } = 60;

    /// <summary>
    /// Shared secret required as <c>Authorization: Bearer &lt;token&gt;</c> on every <c>/api</c> request.
    /// When empty the bridge serves <c>/api</c> to loopback callers only, so a default install keeps
    /// working over <c>adb reverse</c> without a token while never being reachable from the network.
    /// </summary>
    public string? AccessToken { get; set; }

    /// <summary>
    /// Directories under which an agent may be launched. A request whose repo/working directory
    /// falls outside every root is rejected. Empty falls back to the parent of the monitored
    /// repository, which keeps the common "all my repos live in one folder" layout working.
    /// </summary>
    public string[] AllowedRepositoryRoots { get; set; } = [];

    public static string ResolveCodexHome(IConfiguration configuration)
    {
        var configured = configuration["Bridge:CodexHome"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Environment.ExpandEnvironmentVariables(configured);
        }

        var env = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }
}
