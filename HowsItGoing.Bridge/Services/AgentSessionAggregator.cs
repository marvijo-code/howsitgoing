using HowsItGoing.Contracts;
using Microsoft.Extensions.Options;

namespace HowsItGoing.Bridge.Services;

/// <summary>
/// Merges sessions from Codex, Claude Code, and OpenCode into one list.
/// </summary>
public sealed class AgentSessionAggregator
{
    private readonly CodexSessionService _codexSessions;
    private readonly ClaudeCodeSessionService _claudeSessions;
    private readonly OpenCodeSessionService _openCodeSessions;
    private readonly int _maxSessionsPerAgent;
    private readonly ILogger<AgentSessionAggregator> _logger;

    public AgentSessionAggregator(
        CodexSessionService codexSessions,
        ClaudeCodeSessionService claudeSessions,
        OpenCodeSessionService openCodeSessions,
        IOptions<BridgeOptions> options,
        ILogger<AgentSessionAggregator> logger)
    {
        _codexSessions = codexSessions;
        _claudeSessions = claudeSessions;
        _openCodeSessions = openCodeSessions;
        _maxSessionsPerAgent = options.Value.MaxSessionsPerAgent;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CodexSessionSummaryDto>> GetSessionsAsync(
        string? query,
        CodexSessionStatus? status,
        string? source,
        string? agent,
        bool includeArchived,
        CancellationToken cancellationToken)
    {
        var normalizedAgent = AgentKinds.Normalize(agent);
        var tasks = new List<Task<IReadOnlyList<CodexSessionSummaryDto>>>();

        if (normalizedAgent is null or AgentKinds.Codex)
        {
            tasks.Add(GuardedAsync(
                () => _codexSessions.GetSessionsAsync(query, status, source, includeArchived, cancellationToken),
                AgentKinds.Codex));
        }

        if (normalizedAgent is null or AgentKinds.ClaudeCode)
        {
            tasks.Add(GuardedAsync(
                async () => Filter(await _claudeSessions.GetSessionsAsync(cancellationToken), query, status, includeArchived),
                AgentKinds.ClaudeCode));
        }

        if (normalizedAgent is null or AgentKinds.OpenCode)
        {
            tasks.Add(GuardedAsync(
                async () => Filter(await _openCodeSessions.GetSessionsAsync(includeArchived, cancellationToken), query, status, includeArchived),
                AgentKinds.OpenCode));
        }

        var groups = await Task.WhenAll(tasks);
        return groups
            .SelectMany(group => group.Take(_maxSessionsPerAgent))
            .Select(NormalizeDisplayFields)
            .OrderByDescending(session => session.UpdatedAt)
            .ToArray();
    }

    public async Task<CodexSessionSummaryDto?> FindSessionAsync(string agent, string sessionId, CancellationToken cancellationToken)
    {
        var sessions = await GetSessionsAsync(
            query: null,
            status: null,
            source: null,
            agent,
            includeArchived: true,
            cancellationToken);
        return sessions.FirstOrDefault(session =>
            string.Equals(session.Id, sessionId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyList<CodexSessionSummaryDto>> GuardedAsync(
        Func<Task<IReadOnlyList<CodexSessionSummaryDto>>> operation,
        string agent)
    {
        try
        {
            return await operation();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load {Agent} sessions.", agent);
            return [];
        }
    }

    private static IReadOnlyList<CodexSessionSummaryDto> Filter(
        IReadOnlyList<CodexSessionSummaryDto> sessions,
        string? query,
        CodexSessionStatus? status,
        bool includeArchived)
    {
        IEnumerable<CodexSessionSummaryDto> filtered = sessions;

        if (!includeArchived)
        {
            filtered = filtered.Where(session => !session.Archived);
        }

        if (status is { } requiredStatus)
        {
            filtered = filtered.Where(session => session.Status == requiredStatus);
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            var needle = query.Trim();
            filtered = filtered.Where(session =>
                Contains(session.Title, needle) ||
                Contains(session.WorkingDirectory, needle) ||
                Contains(session.GitBranch, needle) ||
                Contains(session.LastAgentMessage, needle));
        }

        return filtered.ToArray();
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static CodexSessionSummaryDto NormalizeDisplayFields(CodexSessionSummaryDto session) =>
        session.WorkingDirectory.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? session with { WorkingDirectory = session.WorkingDirectory[4..] }
            : session;
}
