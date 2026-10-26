using System.Text.Json;
using HowsItGoing.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace HowsItGoing.Bridge.Services;

/// <summary>
/// Reads OpenCode sessions from ~/.local/share/opencode/opencode.db (read-only).
/// </summary>
public sealed class OpenCodeSessionService
{
    private readonly string _databasePath;
    private readonly int _runningThresholdSeconds;
    private readonly int _maxSessions;
    private readonly ILogger<OpenCodeSessionService> _logger;

    public OpenCodeSessionService(
        IConfiguration configuration,
        IOptions<BridgeOptions> options,
        ILogger<OpenCodeSessionService> logger)
    {
        var configured = configuration["Bridge:OpenCodeDbPath"];
        _databasePath = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "opencode", "opencode.db")
            : Environment.ExpandEnvironmentVariables(configured);
        _runningThresholdSeconds = options.Value.RunningThresholdSeconds;
        _maxSessions = options.Value.MaxSessionsPerAgent;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CodexSessionSummaryDto>> GetSessionsAsync(bool includeArchived, CancellationToken cancellationToken)
    {
        if (!File.Exists(_databasePath))
        {
            return [];
        }

        try
        {
            return await QuerySessionsAsync(includeArchived, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read OpenCode sessions from {Path}.", _databasePath);
            return [];
        }
    }

    private async Task<IReadOnlyList<CodexSessionSummaryDto>> QuerySessionsAsync(bool includeArchived, CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var rows = new List<OpenCodeSessionRow>();
        // OpenCode 2.x writes to `session_v2` and leaves the 1.x `session` table behind, so reading
        // `session` alone shows month-old sessions and misses every new run. Prefer v2 when present.
        var sessionsTableIsV2 = await TableExistsAsync(connection, "session_v2", cancellationToken);
        var sessionsTable = sessionsTableIsV2 ? "session_v2" : "session";

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                select id, directory, title, time_created, time_updated, time_archived
                from {sessionsTable}
                where parent_id is null
                  and (@includeArchived = 1 or time_archived is null)
                order by time_updated desc
                limit @take;
                """;
            command.Parameters.AddWithValue("@includeArchived", includeArchived ? 1 : 0);
            command.Parameters.AddWithValue("@take", _maxSessions);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new OpenCodeSessionRow(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                    !reader.IsDBNull(5)));
            }
        }

        var results = new List<CodexSessionSummaryDto>(rows.Count);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lastMessage = sessionsTableIsV2
                ? await ReadLastAssistantMessageV2Async(connection, row.Id, cancellationToken)
                : await ReadLastAssistantMessageAsync(connection, row.Id, cancellationToken);
            results.Add(ToDto(row, lastMessage));
        }

        return results;
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from sqlite_master where type = 'table' and name = @name;";
        command.Parameters.AddWithValue("@name", table);
        var count = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(count, System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>
    /// OpenCode 2.x keeps one row per message in <c>session_message</c> and stores the reply as a
    /// <c>content</c> array, so the answer is the last text item rather than a <c>part</c> row.
    /// </summary>
    private static async Task<string?> ReadLastAssistantMessageV2Async(
        SqliteConnection connection,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select data
            from session_message
            where session_id = @sessionId
              and type = 'assistant'
            order by time_created desc
            limit 1;
            """;
        command.Parameters.AddWithValue("@sessionId", sessionId);

        var data = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(data))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(data);
            if (!document.RootElement.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? text = null;
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var type) &&
                    type.ValueKind == JsonValueKind.String &&
                    type.GetString() == "text" &&
                    item.TryGetProperty("text", out var value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    text = value.GetString();
                }
            }

            return text;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string?> ReadLastAssistantMessageAsync(
        SqliteConnection connection,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select p.data
            from part p
            join message m on m.id = p.message_id
            where m.session_id = @sessionId
              and json_extract(m.data, '$.role') = 'assistant'
              and json_extract(p.data, '$.type') = 'text'
            order by p.id desc
            limit 1;
            """;
        command.Parameters.AddWithValue("@sessionId", sessionId);

        var data = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(data))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(data);
            return document.RootElement.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private CodexSessionSummaryDto ToDto(OpenCodeSessionRow row, string? lastMessage)
    {
        var status = row.Archived
            ? CodexSessionStatus.Archived
            : DateTimeOffset.UtcNow - row.UpdatedAt <= TimeSpan.FromSeconds(_runningThresholdSeconds)
                ? CodexSessionStatus.Running
                : CodexSessionStatus.Idle;

        return new CodexSessionSummaryDto(
            row.Id,
            string.IsNullOrWhiteSpace(row.Title) ? row.Id : row.Title,
            "opencode",
            row.Directory,
            GitBranch: null,
            GitOriginUrl: null,
            AgentNickname: null,
            AgentRole: null,
            row.CreatedAt,
            row.UpdatedAt,
            row.Archived,
            status,
            lastMessage,
            CompletedAt: null,
            AgentKinds.OpenCode);
    }

    private sealed record OpenCodeSessionRow(
        string Id,
        string Directory,
        string? Title,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        bool Archived);
}
