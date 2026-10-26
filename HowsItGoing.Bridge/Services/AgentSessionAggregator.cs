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

    /// <summary>
    /// Reading three agent stores off disk on every call is what made tab switching and paging feel
    /// slow. Four seconds is short enough that a finished run still appears promptly and long enough
    /// that a page tap, a refresh and the shared-store sync collapse onto one read.
    /// </summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(4);

    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private string? _cacheKey;
    private DateTimeOffset _cacheAt;
    private IReadOnlyList<CodexSessionSummaryDto>? _cachedSessions;

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
        var cacheKey = string.Join(
            '\u001f',
            query?.Trim() ?? string.Empty,
            status?.ToString() ?? string.Empty,
            source?.Trim() ?? string.Empty,
            agent?.Trim() ?? string.Empty,
            includeArchived ? "1" : "0");

        if (_cachedSessions is { } cached &&
            string.Equals(_cacheKey, cacheKey, StringComparison.Ordinal) &&
            DateTimeOffset.UtcNow - _cacheAt < CacheLifetime)
        {
            return cached;
        }

        // Serialise the read: a refresh, a page tap and the mirror sync can all arrive together,
        // and without this each one re-reads three agent stores.
        await _cacheGate.WaitAsync(cancellationToken);
        try
        {
            if (_cachedSessions is { } fresh &&
                string.Equals(_cacheKey, cacheKey, StringComparison.Ordinal) &&
                DateTimeOffset.UtcNow - _cacheAt < CacheLifetime)
            {
                return fresh;
            }

            var sessions = await LoadSessionsAsync(query, status, source, agent, includeArchived, cancellationToken);
            _cachedSessions = sessions;
            _cacheKey = cacheKey;
            _cacheAt = DateTimeOffset.UtcNow;
            return sessions;
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    /// <summary>
    /// A page of the merged local feed. The local stores are capped per agent, so this slices in
    /// memory; the shared MySQL mirror uses its own SQL-side paging.
    /// </summary>
    public async Task<SessionPageDto> GetSessionsPageAsync(
        string? query,
        CodexSessionStatus? status,
        string? source,
        string? agent,
        bool includeArchived,
        int offset,
        int limit,
        CancellationToken cancellationToken,
        string? cursor = null)
    {
        // The local feed is a few hundred rows at most, so a slice is cheaper than tracking a
        // cursor. The shared MySQL mirror is the path that needs keyset paging.
        var sessions = await GetSessionsAsync(query, status, source, agent, includeArchived, cancellationToken);
        return SessionPaging.Slice(sessions, offset, limit);
    }

    private async Task<IReadOnlyList<CodexSessionSummaryDto>> LoadSessionsAsync(
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
