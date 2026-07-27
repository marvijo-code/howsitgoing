using HowsItGoing.Contracts;

namespace HowsItGoing.Bridge.Services;

internal static class CodexLaunchCommandBuilder
{
    internal static CodexLaunchCommand Build(StartCodexRunRequest request, string repoPath, bool isGitRepository)
    {
        // Both land in argv, and reasoningEffort is spliced inside a quoted config expression.
        // Validate rather than trust the request shape.
        var model = RequestGuards.ValidateModel(CodexLaunchDefaults.ResolveModel(request.Model));
        var reasoningEffort = RequestGuards.ValidateReasoningEffort(
            CodexLaunchDefaults.ResolveReasoningEffort(request.ReasoningEffort));

        var arguments = new List<string>
        {
            "exec",
            "--json",
            "--full-auto",
            "-C",
            repoPath,
            "--model",
            model,
            "-c",
            $"model_reasoning_effort=\"{reasoningEffort}\""
        };

        if (!isGitRepository && request.AllowOutsideGitRepo)
        {
            arguments.Add("--skip-git-repo-check");
        }

        arguments.Add("-");

        return new CodexLaunchCommand(arguments, request.Prompt);
    }
}

internal sealed record CodexLaunchCommand(IReadOnlyList<string> Arguments, string PromptInput);
