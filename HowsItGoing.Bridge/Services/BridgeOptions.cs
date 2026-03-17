using Microsoft.Extensions.Configuration;

namespace HowsItGoing.Bridge.Services;

public sealed class BridgeOptions
{
    public const string SectionName = "Bridge";

    public int NotificationPollSeconds { get; set; } = 30;

    public int RunningThresholdSeconds { get; set; } = 150;

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
