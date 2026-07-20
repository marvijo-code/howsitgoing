using HowsItGoing.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace HowsItGoing.Bridge.Services;

public sealed class CodexSessionService
{
    private readonly string _stateDbPath;
    private readonly ICodexRuntimeStateProvider _runtimeStateProvider;
    private readonly ILogger<CodexSessionService> _logger;
    private readonly int _maxRuntimeParsers;

    public CodexSessionService(
        IConfiguration configuration,
        ICodexRuntimeStateProvider runtimeStateProvider,
        ILogger<CodexSessionService> logger)
    {
        var codexHome = BridgeOptions.ResolveCodexHome(configuration);
        _stateDbPath = configuration["Bridge:StateDbPath"] ?? Path.Combine(codexHome, "state_5.sqlite");
        _runtimeStateProvider = runtimeStateProvider;
        _logger = logger;
        _maxRuntimeParsers = Math.Clamp(Environment.ProcessorCount, 2, 8);
    }

    public async Task<IReadOnlyList<CodexSessionSummaryDto>> GetSessionsAsync(
        string? query,
        CodexSessionStatus? status,
        string? source,
        bool includeArchived,
        CancellationToken cancellationToken)
    {
        var normalizedQuery = query?.Trim();
        var normalizedSource = source?.Trim();
        var rows = await LoadRowsAsync(normalizedQuery, normalizedSource, includeArchived, null, cancellationToken);
        return await BuildSessionDtosAsync(rows, status, cancellationToken);
    }

    public async Task<IReadOnlyList<CodexSessionSummaryDto>> GetRecentUnarchivedSessionsAsync(int take, CancellationToken cancellationToken)
    {
        var rows = await LoadRowsAsync(query: null, source: null, includeArchived: false, take, cancellationToken);
        return await BuildSessionDtosAsync(rows, status: null, cancellationToken);
    }

    public async Task<bool> ThreadExistsAsync(string threadId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(threadId) || !File.Exists(_stateDbPath))
        {
            return false;
        }

        var connectionString = new SqliteConnectionStringBuilder { DataSource = _stateDbPath }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "select 1 from threads where id = @id limit 1;";
        command.Parameters.AddWithValue("@id", threadId.Trim());
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }

    private async Task<IReadOnlyList<CodexSessionSummaryDto>> BuildSessionDtosAsync(
        List<CodexThreadRow> rows,
        CodexSessionStatus? status,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var results = new CodexSessionSummaryDto?[rows.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, rows.Count),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = _maxRuntimeParsers
            },
            async (index, token) =>
            {
                var row = rows[index];
                var runtimeState = await TryGetRuntimeStateAsync(row, token);
                if (status is { } requiredStatus && runtimeState.Status != requiredStatus)
                {
                    return;
                }

                results[index] = ToDto(row, runtimeState);
            });

        return results.Where(x => x is not null).Select(x => x!).ToArray();
    }

    private async Task<CodexRuntimeState> TryGetRuntimeStateAsync(CodexThreadRow row, CancellationToken cancellationToken)
    {
        try
        {
            return await _runtimeStateProvider.GetRuntimeStateAsync(row.RolloutPath, row.UpdatedAt, row.Archived, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Codex runtime state for thread {ThreadId}. Falling back to database-only state.", row.Id);
            return _runtimeStateProvider.GetFallbackState(row.UpdatedAt, row.Archived);
        }
    }

    private async Task<List<CodexThreadRow>> LoadRowsAsync(
        string? query,
        string? source,
        bool includeArchived,
        int? take,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_stateDbPath))
        {
            return [];
        }

        var rows = new List<CodexThreadRow>();
        var connectionString = new SqliteConnectionStringBuilder { DataSource = _stateDbPath }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var predicates = new List<string>
        {
            "(@includeArchived = 1 or archived = 0)"
        };

        if (!string.IsNullOrWhiteSpace(source))
        {
            predicates.Add("source = @source collate nocase");
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            predicates.Add("""
                (
                    title like @queryPattern escape '\'
                    or cwd like @queryPattern escape '\'
                    or ifnull(git_branch, '') like @queryPattern escape '\'
                    or ifnull(agent_nickname, '') like @queryPattern escape '\'
                    or ifnull(agent_role, '') like @queryPattern escape '\'
                ) collate nocase
                """);
        }

        var sql = @"select id,
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
where
  "
            + string.Join(Environment.NewLine + "  and ", predicates)
            + Environment.NewLine
            + "order by updated_at desc"
            + (take is > 0 ? " limit @take" : string.Empty);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@includeArchived", includeArchived ? 1 : 0);
        if (!string.IsNullOrWhiteSpace(source))
        {
            command.Parameters.AddWithValue("@source", source);
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            command.Parameters.AddWithValue("@queryPattern", $"%{EscapeLikePattern(query)}%");
        }

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

    private static string EscapeLikePattern(string value) =>
        value
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);

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
