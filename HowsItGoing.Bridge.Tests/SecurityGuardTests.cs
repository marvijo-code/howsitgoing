using FluentAssertions;
using HowsItGoing.Bridge.Services;
using NUnit.Framework;

namespace HowsItGoing.Bridge.Tests;

[TestFixture]
public sealed class SecurityGuardTests
{
    [Test]
    public void ValidateSessionId_rejects_values_that_could_reach_an_argument_parser_as_a_flag()
    {
        FluentActions.Invoking(() => RequestGuards.ValidateSessionId("--cd=C:\\"))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => RequestGuards.ValidateSessionId("abc\"&calc&\""))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => RequestGuards.ValidateSessionId("  "))
            .Should().Throw<ArgumentException>();

        RequestGuards.ValidateSessionId(" 1f451300-53ed-4655-ad45-19a0dd84ee99 ")
            .Should().Be("1f451300-53ed-4655-ad45-19a0dd84ee99");
    }

    [Test]
    public void ValidateReasoningEffort_accepts_only_the_known_levels()
    {
        RequestGuards.ValidateReasoningEffort("High").Should().Be("high");

        // This value is spliced inside a quoted codex config expression, so a quote must not survive.
        FluentActions.Invoking(() => RequestGuards.ValidateReasoningEffort("high\" --config x=\""))
            .Should().Throw<ArgumentException>();
    }

    [Test]
    public void ValidateModel_rejects_quotes_and_whitespace()
    {
        RequestGuards.ValidateModel("gpt-5.4").Should().Be("gpt-5.4");
        FluentActions.Invoking(() => RequestGuards.ValidateModel("a b")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => RequestGuards.ValidateModel("a\"b")).Should().Throw<ArgumentException>();
    }

    [Test]
    public void IsWithinAllowedRoots_allows_the_root_and_its_children_only()
    {
        string[] roots = [Path.Combine("C:", "dev")];

        RequestGuards.IsWithinAllowedRoots(Path.Combine("C:", "dev"), roots).Should().BeTrue();
        RequestGuards.IsWithinAllowedRoots(Path.Combine("C:", "dev", "howsitgoing"), roots).Should().BeTrue();

        RequestGuards.IsWithinAllowedRoots(Path.Combine("C:", "Users"), roots).Should().BeFalse();
    }

    [Test]
    public void IsWithinAllowedRoots_rejects_a_sibling_that_merely_shares_the_prefix()
    {
        string[] roots = [Path.Combine("C:", "dev")];

        // "C:\dev-secrets" starts with "C:\dev" as a string but is not inside it.
        RequestGuards.IsWithinAllowedRoots(Path.Combine("C:", "dev-secrets"), roots).Should().BeFalse();
    }

    [Test]
    public void IsWithinAllowedRoots_rejects_traversal_out_of_the_root()
    {
        string[] roots = [Path.Combine("C:", "dev")];

        RequestGuards.IsWithinAllowedRoots(Path.Combine("C:", "dev", "..", "Users"), roots).Should().BeFalse();
    }

    [Test]
    public void IsWithinAllowedRoots_denies_everything_when_no_root_is_configured()
    {
        RequestGuards.IsWithinAllowedRoots(Path.Combine("C:", "dev"), []).Should().BeFalse();
    }

    [Test]
    public void ResolveAllowedRoots_falls_back_to_the_parent_of_the_monitored_repository()
    {
        var options = new BridgeOptions();

        var roots = RequestGuards.ResolveAllowedRoots(options, Path.Combine("C:", "dev", "howsitgoing"));

        roots.Should().ContainSingle().Which.Should().Be(Path.Combine("C:", "dev"));
    }

    [Test]
    public void ResolveAllowedRoots_prefers_explicit_configuration()
    {
        var options = new BridgeOptions { AllowedRepositoryRoots = [Path.Combine("D:", "work")] };

        var roots = RequestGuards.ResolveAllowedRoots(options, Path.Combine("C:", "dev", "howsitgoing"));

        roots.Should().ContainSingle().Which.Should().Be(Path.Combine("D:", "work"));
    }
}
