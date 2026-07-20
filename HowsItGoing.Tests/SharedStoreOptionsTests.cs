using FluentAssertions;
using HowsItGoing.Services;

namespace HowsItGoing.Tests;

public class SharedStoreOptionsTests
{
    private const string SampleJson = """
        {
          "SharedStore": {
            "ConnectionString": "Server=example;Database=db;",
            "CommandPollSeconds": 7,
            "SyncIntervalSeconds": 33,
            "CommandStartTimeoutSeconds": 90
          }
        }
        """;

    [Test]
    public void FromJson_reads_shared_store_section()
    {
        var options = SharedStoreOptions.FromJson(SampleJson);

        options.IsConfigured.Should().BeTrue();
        options.ConnectionString.Should().Be("Server=example;Database=db;");
        options.CommandPollSeconds.Should().Be(7);
        options.SyncIntervalSeconds.Should().Be(33);
        options.CommandStartTimeoutSeconds.Should().Be(90);
    }

    [Test]
    public void FromJson_returns_unconfigured_options_for_invalid_json()
    {
        SharedStoreOptions.FromJson("not json").IsConfigured.Should().BeFalse();
        SharedStoreOptions.FromJson("{}").IsConfigured.Should().BeFalse();
    }

    [Test]
    public void FromEmbeddedResource_returns_unconfigured_options_when_resource_is_missing()
    {
        var options = SharedStoreOptions.FromEmbeddedResource(
            typeof(SharedStoreOptionsTests).Assembly,
            "appsettings.Local.json");

        options.IsConfigured.Should().BeFalse();
    }

    [Test]
    public void FromEmbeddedResource_reads_matching_resource()
    {
        // This test assembly embeds TestData/shared-store.sample.json.
        var options = SharedStoreOptions.FromEmbeddedResource(
            typeof(SharedStoreOptionsTests).Assembly,
            "shared-store.sample.json");

        options.IsConfigured.Should().BeTrue();
        options.ConnectionString.Should().Be("Server=example;Database=db;");
    }
}
