using FluentAssertions;
using HowsItGoing.Bridge.Services;
using HowsItGoing.Contracts;
using NUnit.Framework;

namespace HowsItGoing.Bridge.Tests;

[TestFixture]
public sealed class LaunchProviderTests
{
    private const string RepoPath = @"C:\dev\howsitgoing";

    [Test]
    public void Codex_provider_leaves_the_codex_login_alone()
    {
        var request = CreateRequest(AgentProviders.Codex, "gpt-5.4");

        var command = CodexLaunchCommandBuilder.Build(request, RepoPath, isGitRepository: true);

        command.Arguments.Should().ContainInOrder("exec", "--json", "--model", "gpt-5.4");
        command.Arguments.Should().NotContain(argument => argument.Contains("openrouter"), "the subscription route must not carry OpenRouter overrides");
    }

    [Test]
    public void Codex_openrouter_provider_routes_one_launch_through_openrouter()
    {
        var request = CreateRequest(AgentProviders.CodexOpenRouter, "deepseek/deepseek-v4.1-flash");

        var command = CodexLaunchCommandBuilder.Build(request, RepoPath, isGitRepository: true);

        command.Arguments.Should().Contain("model_provider=\"openrouter\"");
        command.Arguments.Should().Contain("model_providers.openrouter.base_url=\"https://openrouter.ai/api/v1\"");
        command.Arguments.Should().Contain("model_providers.openrouter.env_key=\"OPENROUTER_API_KEY\"");
        command.Arguments.Should().Contain("deepseek/deepseek-v4.1-flash");

        // Every override needs its own -c, otherwise Codex reads the value as a subcommand.
        var configFlagCount = command.Arguments.Count(argument => argument == "-c");
        configFlagCount.Should().Be(8, "seven OpenRouter overrides plus the reasoning effort");
    }

    [Test]
    public void OpenCode_provider_builds_a_headless_run()
    {
        var request = CreateRequest(AgentProviders.OpenCode, "openrouter/deepseek/deepseek-v4.1-flash");

        var command = OpenCodeLaunchCommandBuilder.Build(request);

        command.Arguments.Should().ContainInOrder(
            "run",
            "--auto",
            "--format",
            "json",
            "--model",
            "openrouter/deepseek/deepseek-v4.1-flash");
        command.Arguments.Last().Should().Be(request.Prompt, "the prompt is the final argv element");
    }

    [Test]
    public void Unknown_provider_is_rejected_rather_than_defaulted()
    {
        var act = () => RequestGuards.ValidateProvider("gemini-cli");

        act.Should().Throw<ArgumentException>().WithMessage("*provider must be one of*");
    }

    private static StartCodexRunRequest CreateRequest(string provider, string model) =>
        CodexLaunchDefaults.CreateRequest(
            RepoPath,
            "Summarize the repo and stop.",
            model,
            reasoningEffort: "high",
            provider: provider);
}
