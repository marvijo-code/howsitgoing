using HowsItGoing.Contracts;

namespace HowsItGoing.Bridge.Services;

/// <summary>
/// Builds a headless OpenCode run. `--format json` keeps stdout machine-readable, and the prompt is
/// a single argv element so nothing is interpreted by a shell.
/// </summary>
internal static class OpenCodeLaunchCommandBuilder
{
    internal static OpenCodeLaunchCommand Build(StartCodexRunRequest request)
    {
        var model = RequestGuards.ValidateModel(
            string.IsNullOrWhiteSpace(request.Model)
                ? AgentProviders.DefaultModelFor(AgentProviders.OpenCode)
                : request.Model);

        var arguments = new List<string>
        {
            "run",
            "--auto",
            "--format",
            "json",
            "--model",
            model
        };

        arguments.Add(request.Prompt);

        return new OpenCodeLaunchCommand(arguments, string.Join(' ', arguments));
    }
}

internal sealed record OpenCodeLaunchCommand(IReadOnlyList<string> Arguments, string LaunchMode);
