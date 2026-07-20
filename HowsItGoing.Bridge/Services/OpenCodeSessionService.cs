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
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                select id, directory, title, time_created, time_updated, time_archived
                from session
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
            var lastMessage = await ReadLastAssistantMessageAsync(connection, row.Id, cancellationToken);
            results.Add(ToDto(row, lastMessage));
        }

        return results;
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
