using FluentAssertions;
using HowsItGoing.Bridge.Services;
using HowsItGoing.Contracts;
using NUnit.Framework;

namespace HowsItGoing.Bridge.Tests;

[TestFixture]
public sealed class IssueBoardTests
{
    [Test]
    public void ParseClosingReferences_extracts_all_closing_keywords()
    {
        var refs = GitHubIssueService.ParseClosingReferences(
            "Fix login redirect (closes #12)",
            "This also Fixes #34 and resolves #34 and Resolved: #7.\nMentions #99 without a keyword should be ignored.");

        refs.Should().BeEquivalentTo([12, 34, 7]);
        refs.Should().NotContain(99);
    }

    [Test]
    public void ParseClosingReferences_returns_empty_when_no_keyword()
    {
        GitHubIssueService.ParseClosingReferences("Just a title referencing #5", null).Should().BeEmpty();
        GitHubIssueService.ParseClosingReferences(null, null).Should().BeEmpty();
    }

    [Test]
    public void MapPullRequestState_maps_merged_closed_and_open()
    {
        GitHubIssueService.MapPullRequestState("closed", DateTimeOffset.UtcNow).Should().Be(PullRequestState.Merged);
        GitHubIssueService.MapPullRequestState("closed", null).Should().Be(PullRequestState.Closed);
        GitHubIssueService.MapPullRequestState("open", null).Should().Be(PullRequestState.Open);
    }

    [Test]
    public void RollupChecks_prioritises_failure_then_pending_then_success()
    {
        GitHubIssueService.RollupChecks([]).Should().Be(CheckRollupStatus.None);
        GitHubIssueService.RollupChecks([("completed", "success"), ("completed", "skipped")]).Should().Be(CheckRollupStatus.Success);
        GitHubIssueService.RollupChecks([("completed", "success"), ("in_progress", null)]).Should().Be(CheckRollupStatus.Pending);
        GitHubIssueService.RollupChecks([("completed", "success"), ("completed", "failure")]).Should().Be(CheckRollupStatus.Failure);
        GitHubIssueService.RollupChecks([("queued", null)]).Should().Be(CheckRollupStatus.Pending);
    }

    [Test]
    public void CorrelateAgents_matches_sessions_on_the_same_branch_only()
    {
        var sessions = new[]
        {
            Session("codex-1", AgentKinds.Codex, "fix/login"),
            Session("claude-1", AgentKinds.ClaudeCode, "fix/login"),
            Session("codex-2", AgentKinds.Codex, "main")
        };

        var agents = GitHubIssueService.CorrelateAgents(sessions, "fix/login");

        agents.Select(a => a.SessionId).Should().BeEquivalentTo(["codex-1", "claude-1"]);
        GitHubIssueService.CorrelateAgents(sessions, null).Should().BeEmpty();
        GitHubIssueService.CorrelateAgents([], "fix/login").Should().BeEmpty();
    }

    [Test]
    public void BranchTargetsIssue_matches_explicit_markers_with_boundaries()
    {
        GitHubIssueService.BranchTargetsIssue("123-fix-the-thing", 123).Should().BeTrue();
        GitHubIssueService.BranchTargetsIssue("fix/issue-42-crash", 42).Should().BeTrue();
        GitHubIssueService.BranchTargetsIssue("gh-7", 7).Should().BeTrue();

        // Boundaries: issue 1 must not match issue-12, and a bare version suffix must not match.
        GitHubIssueService.BranchTargetsIssue("fix/issue-12", 1).Should().BeFalse();
        GitHubIssueService.BranchTargetsIssue("fix/pin-github-android-build-to-net9", 9).Should().BeFalse();
        GitHubIssueService.BranchTargetsIssue(null, 5).Should().BeFalse();
    }

    [Test]
    public void ResolveRepositories_dedupes_configured_repos_and_applies_filter()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), $"hig-issues-{Guid.NewGuid():N}");
        Directory.CreateDirectory(contentRoot);

        try
        {
            var configured = new[]
            {
                "marvijo-code/alpha",
                "https://github.com/marvijo-code/beta.git",
                "marvijo-code/alpha" // duplicate
            };

            var all = GitHubIssueService.ResolveRepositories(contentRoot, "..", configured, filter: null);
            all.Select(r => $"{r.Owner}/{r.Name}")
                .Should().BeEquivalentTo(["marvijo-code/alpha", "marvijo-code/beta"]);

            var filtered = GitHubIssueService.ResolveRepositories(contentRoot, "..", configured, filter: ["marvijo-code/beta"]);
            filtered.Select(r => $"{r.Owner}/{r.Name}").Should().Equal("marvijo-code/beta");

            // An explicit filter can request a repo that is not in the configured set.
            var adHoc = GitHubIssueService.ResolveRepositories(contentRoot, "..", configured, filter: ["marvijo-code/gamma", "marvijo-code/gamma"]);
            adHoc.Select(r => $"{r.Owner}/{r.Name}").Should().Equal("marvijo-code/gamma");
        }
        finally
        {
            try
            {
                Directory.Delete(contentRoot, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static CodexSessionSummaryDto Session(string id, string agent, string branch) =>
        new(
            id,
            $"Title {id}",
            "vscode",
            @"C:\dev\repo",
            branch,
            "https://github.com/marvijo-code/repo.git",
            null,
            null,
            DateTimeOffset.UtcNow.AddMinutes(-10),
            DateTimeOffset.UtcNow,
            false,
            CodexSessionStatus.Running,
            null,
            null,
            agent);
}
