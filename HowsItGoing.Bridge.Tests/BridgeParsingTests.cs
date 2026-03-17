using HowsItGoing.Bridge.Services;
using HowsItGoing.Contracts;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace HowsItGoing.Bridge.Tests;

[TestFixture]
public sealed class BridgeParsingTests
{
    [Test]
    public async Task CodexThreadParser_detects_task_complete_events()
    {
        var filePath = Path.GetTempFileName();
        try
        {
            await File.WriteAllLinesAsync(filePath,
            [
                "{\"timestamp\":\"2026-03-16T23:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\",\"message\":\"Working\"}}",
                "{\"timestamp\":\"2026-03-16T23:05:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"last_agent_message\":\"Done\"}}"
            ]);

            var parser = new CodexThreadParser(Options.Create(new BridgeOptions { RunningThresholdSeconds = 90 }));
            var result = await parser.GetRuntimeStateAsync(filePath, DateTimeOffset.UtcNow, archived: false, CancellationToken.None);

            result.Status.Should().Be(CodexSessionStatus.Completed);
            result.LastAgentMessage.Should().Be("Done");
            result.CompletedAt.Should().Be(DateTimeOffset.Parse("2026-03-16T23:05:00Z"));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [TestCase("https://github.com/marvijo-code/howsitgoing.git", "marvijo-code", "howsitgoing")]
    [TestCase("git@github.com:marvijo-code/howsitgoing.git", "marvijo-code", "howsitgoing")]
    public void GitHub_remote_slug_is_extracted(string remoteUrl, string owner, string name)
    {
        var result = GitHubMonitorOptions.TryExtractRepositorySlug(remoteUrl);

        result.Should().NotBeNull();
        result!.Value.Owner.Should().Be(owner);
        result.Value.Name.Should().Be(name);
    }
}
