using HowsItGoing.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace HowsItGoing.Bridge.Services;

public sealed class CodexSessionService
{
    private readonly string _stateDbPath;
    private readonly CodexThreadParser _threadParser;

    public CodexSessionService(IConfiguration configuration, CodexThreadParser threadParser)
    {
        var codexHome = BridgeOptions.ResolveCodexHome(configuration);
        _stateDbPath = configuration["Bridge:StateDbPath"] ?? Path.Combine(codexHome, "state_5.sqlite");
        _threadParser = threadParser;
    }

    public async Task<IReadOnlyList<CodexSessionSummaryDto>> GetSessionsAsync(
        string? query,
        CodexSessionStatus? status,
        string? source,
        bool includeArchived,
        CancellationToken cancellationToken)
    {
        var rows = await LoadRowsAsync(includeArchived, null, cancellationToken);
        var normalizedQuery = query?.Trim();
        var normalizedSource = source?.Trim();
        var results = new List<CodexSessionSummaryDto>(rows.Count);

        foreach (var row in rows)
        {
            if (!MatchesQuery(row, normalizedQuery) || !MatchesSource(row, normalizedSource))
            {
                continue;
            }

            var runtimeState = await _threadParser.GetRuntimeStateAsync(row.RolloutPath, row.UpdatedAt, row.Archived, cancellationToken);
            if (status is { } requiredStatus && runtimeState.Status != requiredStatus)
            {
                continue;
            }

            results.Add(ToDto(row, runtimeState));
        }

        return results.OrderByDescending(x => x.UpdatedAt).ToArray();
    }

    public async Task<IReadOnlyList<CodexSessionSummaryDto>> GetRecentUnarchivedSessionsAsync(int take, CancellationToken cancellationToken)
    {
        var rows = await LoadRowsAsync(includeArchived: false, take, cancellationToken);
        var results = new List<CodexSessionSummaryDto>(rows.Count);
        foreach (var row in rows)
        {
            var runtimeState = await _threadParser.GetRuntimeStateAsync(row.RolloutPath, row.UpdatedAt, row.Archived, cancellationToken);
            results.Add(ToDto(row, runtimeState));
        }

        return results;
    }

    private async Task<List<CodexThreadRow>> LoadRowsAsync(bool includeArchived, int? take, CancellationToken cancellationToken)
    {
        if (!File.Exists(_stateDbPath))
        {
            return [];
        }

        var rows = new List<CodexThreadRow>();
        var connectionString = new SqliteConnectionStringBuilder { DataSource = _stateDbPath }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = """
            select id,
                   rollout_path,
                   created_at,
                   updated_at,
                   source,
                   cwd,
                   title,
                   archived,
                   git_branch,
                   git_origin_url,
                   agent_nickname,
                   agent_role
            from threads
            where (@includeArchived = 1 or archived = 0)
            order by updated_at desc
            """
            + (take is > 0 ? " limit @take" : string.Empty);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@includeArchived", includeArchived ? 1 : 0);
        if (take is > 0)
        {
            command.Parameters.AddWithValue("@take", take.Value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CodexThreadRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetInt32(7) != 0,
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        }

        return rows;
    }

    private static CodexSessionSummaryDto ToDto(CodexThreadRow row, CodexRuntimeState runtimeState) =>
        new(
            row.Id,
            row.Title,
            row.Source,
            row.Cwd,
            row.GitBranch,
            row.GitOriginUrl,
            row.AgentNickname,
            row.AgentRole,
            row.CreatedAt,
            row.UpdatedAt,
            row.Archived,
            runtimeState.Status,
            runtimeState.LastAgentMessage,
            runtimeState.CompletedAt);

    private static bool MatchesQuery(CodexThreadRow row, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return row.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               row.Cwd.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               (row.GitBranch?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (row.AgentNickname?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (row.AgentRole?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static bool MatchesSource(CodexThreadRow row, string? source) =>
        string.IsNullOrWhiteSpace(source) || row.Source.Equals(source, StringComparison.OrdinalIgnoreCase);

    private sealed record CodexThreadRow(
        string Id,
        string? RolloutPath,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        string Source,
        string Cwd,
        string Title,
        bool Archived,
        string? GitBranch,
        string? GitOriginUrl,
        string? AgentNickname,
        string? AgentRole);
}
