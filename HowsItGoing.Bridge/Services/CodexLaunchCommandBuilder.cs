using HowsItGoing.Contracts;

namespace HowsItGoing.Bridge.Services;

internal static class CodexLaunchCommandBuilder
{
    /// <summary>
    /// Codex `-c` overrides that route one launch through OpenRouter. They are applied per launch
    /// and never written to `~/.codex/config.toml`, so the machine's own Codex login and the
    /// desktop app keep using the subscription. Same token set as the T3 Code OpenRouter provider
    /// (`apps/server/src/provider/openRouterInstances.ts`); the values are TOML, hence the quotes.
    /// </summary>
    private static readonly string[] OpenRouterConfigOverrides =
    [
        "model_provider=\"openrouter\"",
        "model_providers.openrouter.name=\"OpenRouter\"",
        "model_providers.openrouter.base_url=\"https://openrouter.ai/api/v1\"",
        "model_providers.openrouter.env_key=\"OPENROUTER_API_KEY\"",
        "model_providers.openrouter.wire_api=\"responses\"",
        "model_providers.openrouter.requires_openai_auth=false",
        "model_providers.openrouter.supports_websockets=false"
    ];

    internal static CodexLaunchCommand Build(StartCodexRunRequest request, string repoPath, bool isGitRepository)
    {
        var provider = RequestGuards.ValidateProvider(request.Provider);
        var usesOpenRouter = provider == AgentProviders.CodexOpenRouter;

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

        if (usesOpenRouter)
        {
            foreach (var overrideValue in OpenRouterConfigOverrides)
            {
                arguments.Add("-c");
                arguments.Add(overrideValue);
            }
        }

        if (!isGitRepository && request.AllowOutsideGitRepo)
        {
            arguments.Add("--skip-git-repo-check");
        }

        arguments.Add("-");

        return new CodexLaunchCommand(arguments, request.Prompt);
    }
}

internal sealed record CodexLaunchCommand(IReadOnlyList<string> Arguments, string PromptInput);
