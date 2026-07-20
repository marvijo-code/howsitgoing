using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using HowsItGoing.Contracts;
using Microsoft.Extensions.Options;

namespace HowsItGoing.Bridge.Services;

/// <summary>
/// Builds the issues/PR board: open + closed issues and their pull requests across a
/// configurable set of repositories, linked to each other and to the live agent sessions
/// (Codex / Claude Code / OpenCode) that are working on the matching branch.
/// </summary>
public sealed partial class GitHubIssueService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AgentSessionAggregator _sessions;
    private readonly GitHubMonitorOptions _options;
    private readonly string _contentRoot;
    private readonly ILogger<GitHubIssueService> _logger;

    public GitHubIssueService(
        IHttpClientFactory httpClientFactory,
        AgentSessionAggregator sessions,
        IOptions<GitHubMonitorOptions> options,
        IHostEnvironment environment,
        ILogger<GitHubIssueService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _sessions = sessions;
        _options = options.Value;
        _contentRoot = environment.ContentRootPath;
        _logger = logger;
    }

    public async Task<IssueBoardDto> GetBoardAsync(
        IReadOnlyList<string>? repositoryFilter,
        string? state,
        CancellationToken cancellationToken)
    {
        var repositories = ResolveRepositories(_contentRoot, _options.MonitoredRepositoryPath, _options.IssueRepositories, repositoryFilter);
        var wantState = NormalizeState(state);

        var liveSessions = await _sessions.GetSessionsAsync(
            query: null, status: null, source: null, agent: null, includeArchived: false, cancellationToken);
        var sessionsByRepo = GroupSessionsByRepository(liveSessions);

        var token = await TryGetGitHubTokenAsync(cancellationToken);
        var checkBudget = Math.Max(0, _options.MaxCheckLookups);

        var repoResults = new List<IssueBoardRepositoryDto>();
        var allIssues = new List<IssueSummaryDto>();
        var allPullRequests = new List<PullRequestSummaryDto>();

        foreach (var (owner, name) in repositories)
        {
            var repoKey = $"{owner}/{name}";
            sessionsByRepo.TryGetValue(repoKey, out var repoSessions);
            repoSessions ??= [];

            try
            {
                var pulls = await FetchPullRequestsAsync(owner, name, wantState, token, repoSessions, checkBudget, cancellationToken);
                checkBudget -= pulls.CheckLookupsUsed;
                var issues = await FetchIssuesAsync(owner, name, wantState, token, repoSessions, pulls.PullRequests, cancellationToken);

                allPullRequests.AddRange(pulls.PullRequests);
                allIssues.AddRange(issues);
                repoResults.Add(new IssueBoardRepositoryDto(
                    owner,
                    name,
                    IsConfigured: true,
                    OpenIssueCount: issues.Count(i => i.State == IssueState.Open),
                    ClosedIssueCount: issues.Count(i => i.State == IssueState.Closed),
                    OpenPullRequestCount: pulls.PullRequests.Count(p => p.State == PullRequestState.Open),
                    Error: null));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load issues for {Repository}.", repoKey);
                repoResults.Add(new IssueBoardRepositoryDto(owner, name, true, 0, 0, 0, ex.Message));
            }
        }

        var orderedIssues = allIssues
            .OrderBy(i => i.State)
            .ThenByDescending(i => i.UpdatedAt)
            .ToArray();
        var orderedPulls = allPullRequests
            .OrderBy(p => p.State == PullRequestState.Open ? 0 : 1)
            .ThenByDescending(p => p.UpdatedAt)
            .ToArray();

        return new IssueBoardDto(repoResults, orderedIssues, orderedPulls, DateTimeOffset.UtcNow);
    }

    // ---------- Fetching ----------

    private sealed record PullRequestFetch(IReadOnlyList<PullRequestSummaryDto> PullRequests, int CheckLookupsUsed);

    private async Task<PullRequestFetch> FetchPullRequestsAsync(
        string owner,
        string name,
        string state,
        string? token,
        IReadOnlyList<CodexSessionSummaryDto> repoSessions,
        int checkBudget,
        CancellationToken cancellationToken)
    {
        var url = $"/repos/{owner}/{name}/pulls?state={state}&per_page={_options.IssuesPerRepository}&sort=updated&direction=desc";
        using var response = await SendGitHubRequestAsync(url, token, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return new PullRequestFetch([], 0);
        }

        var result = new List<PullRequestSummaryDto>();
        var checksUsed = 0;
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var number = element.GetProperty("number").GetInt32();
            var title = GetString(element, "title") ?? $"Pull request #{number}";
            var draft = element.TryGetProperty("draft", out var draftValue) && draftValue.ValueKind == JsonValueKind.True;
            var mergedAt = GetDate(element, "merged_at");
            var closedAt = GetDate(element, "closed_at");
            var rawState = GetString(element, "state");
            var prState = MapPullRequestState(rawState, mergedAt);
            var author = GetLogin(element, "user");
            var assignees = GetAssignees(element);
            var headBranch = element.TryGetProperty("head", out var head) ? GetString(head, "ref") : null;
            var headSha = element.TryGetProperty("head", out var head2) ? GetString(head2, "sha") : null;
            var baseBranch = element.TryGetProperty("base", out var baseEl) ? GetString(baseEl, "ref") : null;
            var htmlUrl = GetString(element, "html_url") ?? $"https://github.com/{owner}/{name}/pull/{number}";
            var linkedIssues = ParseClosingReferences(title, GetString(element, "body"));
            var workingAgents = CorrelateAgents(repoSessions, headBranch);

            var checks = CheckRollupStatus.None;
            if (prState == PullRequestState.Open && !draft && checksUsed < checkBudget && !string.IsNullOrWhiteSpace(headSha))
            {
                checks = await FetchCheckRollupAsync(owner, name, headSha!, token, cancellationToken);
                checksUsed++;
            }

            result.Add(new PullRequestSummaryDto(
                $"{owner}/{name}", number, title, prState, draft, author, assignees,
                headBranch, baseBranch, htmlUrl,
                GetDate(element, "created_at") ?? DateTimeOffset.UtcNow,
                GetDate(element, "updated_at") ?? DateTimeOffset.UtcNow,
                closedAt, mergedAt, checks, linkedIssues, workingAgents));
        }

        return new PullRequestFetch(result, checksUsed);
    }

    private async Task<IReadOnlyList<IssueSummaryDto>> FetchIssuesAsync(
        string owner,
        string name,
        string state,
        string? token,
        IReadOnlyList<CodexSessionSummaryDto> repoSessions,
        IReadOnlyList<PullRequestSummaryDto> pullRequests,
        CancellationToken cancellationToken)
    {
        var url = $"/repos/{owner}/{name}/issues?state={state}&per_page={_options.IssuesPerRepository}&sort=updated&direction=desc";
        using var response = await SendGitHubRequestAsync(url, token, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<IssueSummaryDto>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            // The issues endpoint also returns pull requests; skip those.
            if (element.TryGetProperty("pull_request", out _))
            {
                continue;
            }

            var number = element.GetProperty("number").GetInt32();
            var issueState = string.Equals(GetString(element, "state"), "closed", StringComparison.OrdinalIgnoreCase)
                ? IssueState.Closed
                : IssueState.Open;
            var title = GetString(element, "title") ?? $"Issue #{number}";
            var author = GetLogin(element, "user");
            var assignees = GetAssignees(element);
            var labels = GetLabels(element);
            var htmlUrl = GetString(element, "html_url") ?? $"https://github.com/{owner}/{name}/issues/{number}";

            var linkedPrs = pullRequests
                .Where(pr => pr.LinkedIssueNumbers.Contains(number))
                .Select(pr => pr.Number)
                .Distinct()
                .OrderBy(n => n)
                .ToArray();

            var agents = MergeIssueAgents(number, linkedPrs, pullRequests, repoSessions);

            result.Add(new IssueSummaryDto(
                $"{owner}/{name}", number, title, issueState, author, assignees, labels, htmlUrl,
                GetDate(element, "created_at") ?? DateTimeOffset.UtcNow,
                GetDate(element, "updated_at") ?? DateTimeOffset.UtcNow,
                GetDate(element, "closed_at"),
                linkedPrs, agents));
        }

        return result;
    }

    private async Task<CheckRollupStatus> FetchCheckRollupAsync(
        string owner,
        string name,
        string sha,
        string? token,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendGitHubRequestAsync($"/repos/{owner}/{name}/commits/{sha}/check-runs", token, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return CheckRollupStatus.None;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("check_runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
            {
                return CheckRollupStatus.None;
            }

            var statuses = new List<(string? Status, string? Conclusion)>();
            foreach (var run in runs.EnumerateArray())
            {
                statuses.Add((GetString(run, "status"), GetString(run, "conclusion")));
            }

            return RollupChecks(statuses);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to load check runs for {Owner}/{Name}@{Sha}.", owner, name, sha);
            return CheckRollupStatus.None;
        }
    }

    // ---------- Testable pure helpers ----------

    /// <summary>
    /// Resolves the distinct repositories to show. When an explicit filter is supplied
    /// (e.g. the app's repo dropdown or an ad-hoc <c>?repo=owner/name</c>), exactly those
    /// repositories are returned; otherwise it falls back to the monitored repo + configured extras.
    /// </summary>
    public static IReadOnlyList<(string Owner, string Name)> ResolveRepositories(
        string contentRoot,
        string? monitoredRepositoryPath,
        IEnumerable<string>? configuredRepositories,
        IReadOnlyList<string>? filter)
    {
        var ordered = new List<(string Owner, string Name)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add((string Owner, string Name)? parsed)
        {
            if (parsed is null)
            {
                return;
            }

            var key = $"{parsed.Value.Owner}/{parsed.Value.Name}";
            if (seen.Add(key))
            {
                ordered.Add(parsed.Value);
            }
        }

        if (filter is { Count: > 0 })
        {
            foreach (var repo in filter)
            {
                Add(TryParseSlug(repo));
            }

            return ordered;
        }

        var monitoredPath = GitHubMonitorOptions.ResolveMonitoredRepositoryPath(contentRoot, monitoredRepositoryPath);
        Add(GitHubMonitorOptions.TryExtractRepositorySlug(GitHubMonitorOptions.ResolveRemoteUrl(monitoredPath)));

        if (configuredRepositories is not null)
        {
            foreach (var repo in configuredRepositories)
            {
                Add(TryParseSlug(repo));
            }
        }

        return ordered;
    }

    /// <summary>Parses GitHub closing keywords (Closes/Fixes/Resolves #N) from a PR title and body.</summary>
    public static IReadOnlyList<int> ParseClosingReferences(string? title, string? body)
    {
        var text = $"{title}\n{body}";
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var numbers = new List<int>();
        foreach (Match match in ClosingReferenceRegex().Matches(text))
        {
            if (int.TryParse(match.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
                !numbers.Contains(value))
            {
                numbers.Add(value);
            }
        }

        return numbers;
    }

    public static PullRequestState MapPullRequestState(string? rawState, DateTimeOffset? mergedAt)
    {
        if (mergedAt is not null)
        {
            return PullRequestState.Merged;
        }

        return string.Equals(rawState, "closed", StringComparison.OrdinalIgnoreCase)
            ? PullRequestState.Closed
            : PullRequestState.Open;
    }

    public static CheckRollupStatus RollupChecks(IReadOnlyList<(string? Status, string? Conclusion)> runs)
    {
        if (runs.Count == 0)
        {
            return CheckRollupStatus.None;
        }

        var anyPending = false;
        var anyFailure = false;
        var anySuccess = false;
        foreach (var (status, conclusion) in runs)
        {
            if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                anyPending = true;
                continue;
            }

            switch (conclusion?.ToLowerInvariant())
            {
                case "success":
                case "neutral":
                case "skipped":
                    anySuccess = true;
                    break;
                case "failure":
                case "timed_out":
                case "cancelled":
                case "action_required":
                case "startup_failure":
                    anyFailure = true;
                    break;
                default:
                    anyPending = true;
                    break;
            }
        }

        if (anyFailure)
        {
            return CheckRollupStatus.Failure;
        }

        if (anyPending)
        {
            return CheckRollupStatus.Pending;
        }

        return anySuccess ? CheckRollupStatus.Success : CheckRollupStatus.None;
    }

    /// <summary>Live sessions on the same repo whose git branch equals the PR head branch.</summary>
    public static IReadOnlyList<AgentAssignmentDto> CorrelateAgents(
        IReadOnlyList<CodexSessionSummaryDto> repoSessions,
        string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch) || repoSessions.Count == 0)
        {
            return [];
        }

        return repoSessions
            .Where(s => !string.IsNullOrWhiteSpace(s.GitBranch) &&
                        string.Equals(s.GitBranch, branch, StringComparison.OrdinalIgnoreCase))
            .Select(s => new AgentAssignmentDto(s.Agent, s.Id, s.Title, s.GitBranch, s.Status))
            .ToArray();
    }

    /// <summary>True when a branch name explicitly targets the given issue number (issue-N, gh-N, N-title...).</summary>
    public static bool BranchTargetsIssue(string? branch, int issueNumber)
    {
        if (string.IsNullOrWhiteSpace(branch))
        {
            return false;
        }

        var normalized = branch.Trim().ToLowerInvariant();
        var n = issueNumber.ToString(CultureInfo.InvariantCulture);

        if (normalized.StartsWith(n + "-", StringComparison.Ordinal))
        {
            return true;
        }

        string[] markers = [$"issue-{n}", $"issue/{n}", $"issue_{n}", $"issue{n}", $"gh-{n}", $"gh/{n}"];
        foreach (var marker in markers)
        {
            var index = normalized.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            var after = index + marker.Length;
            if (after >= normalized.Length || !char.IsDigit(normalized[after]))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<AgentAssignmentDto> MergeIssueAgents(
        int issueNumber,
        IReadOnlyList<int> linkedPrNumbers,
        IReadOnlyList<PullRequestSummaryDto> pullRequests,
        IReadOnlyList<CodexSessionSummaryDto> repoSessions)
    {
        var byKey = new Dictionary<string, AgentAssignmentDto>(StringComparer.OrdinalIgnoreCase);

        foreach (var pr in pullRequests.Where(pr => linkedPrNumbers.Contains(pr.Number)))
        {
            foreach (var agent in pr.WorkingAgents)
            {
                byKey[agent.SessionId] = agent;
            }
        }

        foreach (var session in repoSessions.Where(s => BranchTargetsIssue(s.GitBranch, issueNumber)))
        {
            byKey[session.Id] = new AgentAssignmentDto(session.Agent, session.Id, session.Title, session.GitBranch, session.Status);
        }

        return byKey.Values.ToArray();
    }

    private static Dictionary<string, List<CodexSessionSummaryDto>> GroupSessionsByRepository(
        IReadOnlyList<CodexSessionSummaryDto> sessions)
    {
        var map = new Dictionary<string, List<CodexSessionSummaryDto>>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in sessions)
        {
            var slug = GitHubMonitorOptions.TryExtractRepositorySlug(session.GitOriginUrl);
            if (slug is null)
            {
                continue;
            }

            var key = $"{slug.Value.Owner}/{slug.Value.Name}";
            if (!map.TryGetValue(key, out var list))
            {
                list = [];
                map[key] = list;
            }

            list.Add(session);
        }

        return map;
    }

    private static (string Owner, string Name)? TryParseSlug(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return GitHubMonitorOptions.TryExtractRepositorySlug(trimmed);
        }

        var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2 ? (parts[0], parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1]) : null;
    }

    private static string NormalizeState(string? state) => state?.Trim().ToLowerInvariant() switch
    {
        "open" => "open",
        "closed" => "closed",
        _ => "all"
    };

    // ---------- JSON helpers ----------

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? GetDate(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    private static string? GetLogin(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty("login", out var login) &&
        login.ValueKind == JsonValueKind.String
            ? login.GetString()
            : null;

    private static IReadOnlyList<string> GetAssignees(JsonElement element)
    {
        if (!element.TryGetProperty("assignees", out var assignees) || assignees.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var assignee in assignees.EnumerateArray())
        {
            if (assignee.TryGetProperty("login", out var login) && login.ValueKind == JsonValueKind.String)
            {
                var name = login.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    result.Add(name);
                }
            }
        }

        return result;
    }

    private static IReadOnlyList<string> GetLabels(JsonElement element)
    {
        if (!element.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var label in labels.EnumerateArray())
        {
            var name = label.ValueKind == JsonValueKind.String
                ? label.GetString()
                : label.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String
                    ? nameValue.GetString()
                    : null;
            if (!string.IsNullOrWhiteSpace(name))
            {
                result.Add(name);
            }
        }

        return result;
    }

    // ---------- GitHub transport (mirrors GitHubRepositoryService) ----------

    private async Task<string?> TryGetGitHubTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "gh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("auth");
            startInfo.ArgumentList.Add("token");

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to retrieve GitHub token from gh.");
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendGitHubRequestAsync(string relativeUrl, string? token, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri("https://api.github.com");
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HowsItGoingBridge", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (!string.IsNullOrWhiteSpace(token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.GetAsync(relativeUrl, cancellationToken);
    }

    [GeneratedRegex(@"(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s*:?\s+#(?<n>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClosingReferenceRegex();
}
