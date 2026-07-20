using FluentAssertions;
using HowsItGoing.Contracts;

namespace HowsItGoing.Tests;

public class AppSettingsTests
{
    [Test]
    public void AppSettings_defaults_to_gpt_5_4_high()
    {
        var settings = new AppSettings();

        settings.AgentModel.Should().Be(CodexLaunchDefaults.DefaultModel);
        settings.AgentReasoningEffort.Should().Be(CodexLaunchDefaults.DefaultReasoningEffort);
    }

    [Test]
    public void CodexLaunchDefaults_create_request_trims_and_applies_defaults()
    {
        var request = CodexLaunchDefaults.CreateRequest(
            @" C:\dev\howsitgoing ",
            " Reply with exactly: ok \n",
            model: " ",
            reasoningEffort: null);

        request.RepoPath.Should().Be(@"C:\dev\howsitgoing");
        request.Prompt.Should().Be("Reply with exactly: ok");
        request.Model.Should().Be(CodexLaunchDefaults.DefaultModel);
        request.ReasoningEffort.Should().Be(CodexLaunchDefaults.DefaultReasoningEffort);
    }
}
