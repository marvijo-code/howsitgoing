using System.Data;
using HowsItGoing.Contracts;
using MySqlConnector;

namespace HowsItGoing.Services;

public sealed class SharedBridgeStore
{
    private readonly SharedStoreOptions _options;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private volatile bool _schemaEnsured;

    public SharedBridgeStore(SharedStoreOptions options)
    {
        _options = options;
    }

    public bool IsConfigured => _options.IsConfigured;

    public int CommandPollSeconds => _options.CommandPollSeconds;

    public int SyncIntervalSeconds => _options.SyncIntervalSeconds;

    public int CommandStartTimeoutSeconds => _options.CommandStartTimeoutSeconds;

    public async Task<IReadOnlyList<CodexSessionSummaryDto>> GetSessionsAsync(
        string? query,
        string? status,
        string? source,
        string? agent,
        bool includeArchived,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return [];
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var predicates = new List<string>
        {
            "(@includeArchived = 1 or archived = 0)"
        };

        if (!string.IsNullOrWhiteSpace(source))
        {
            predicates.Add("source = @source");
        }

        if (!string.IsNullOrWhiteSpace(agent))
        {
            predicates.Add("agent = @agent");
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            predicates.Add("status = @status");
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            predicates.Add("""
                (
                    lower(title) like @queryPattern
                    or lower(working_directory) like @queryPattern
                    or lower(coalesce(git_branch, '')) like @queryPattern
                    or lower(coalesce(agent_nickname, '')) like @queryPattern
                    or lower(coalesce(agent_role, '')) like @queryPattern
                )
                """);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            select
                id,
                title,
                source,
                working_directory,
                git_branch,
                git_origin_url,
                agent_nickname,
                agent_role,
                created_at,
                updated_at,
                archived,
                status,
                last_agent_message,
                completed_at,
                agent
            from howsitgoing_sessions
            where
            """
            + string.Join(Environment.NewLine + "  and ", predicates)
            + Environment.NewLine
            + "order by updated_at desc";
        command.Parameters.AddWithValue("@includeArchived", includeArchived ? 1 : 0);
        if (!string.IsNullOrWhiteSpace(source))
        {
            command.Parameters.AddWithValue("@source", source.Trim());
        }

        if (!string.IsNullOrWhiteSpace(agent))
        {
            command.Parameters.AddWithValue("@agent", AgentKinds.Normalize(agent));
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            command.Parameters.AddWithValue("@status", status.Trim());
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            command.Parameters.AddWithValue("@queryPattern", $"%{query.Trim().ToLowerInvariant()}%");
        }

        var results = new List<CodexSessionSummaryDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new CodexSessionSummaryDto(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                ReadDateTimeOffset(reader, 8),
                ReadDateTimeOffset(reader, 9),
                reader.GetBoolean(10),
                Enum.TryParse<CodexSessionStatus>(reader.GetString(11), ignoreCase: true, out var sessionStatus)
                    ? sessionStatus
                    : CodexSessionStatus.Idle,
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : ReadDateTimeOffset(reader, 13),
                reader.IsDBNull(14) ? AgentKinds.Codex : reader.GetString(14)));
        }

        return results;
    }

    public async Task<IReadOnlyList<BridgeNotificationDto>> GetNotificationsAsync(
        DateTimeOffset? since,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return [];
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select
                id,
                kind,
                title,
                message,
                occurred_at,
                related_session_id,
                related_repository,
                related_url
            from howsitgoing_notifications
            where (@since is null or occurred_at > @since)
            order by occurred_at desc
            limit @limit;
            """;
        command.Parameters.AddWithValue("@since", since?.UtcDateTime);
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 200));

        var results = new List<BridgeNotificationDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new BridgeNotificationDto(
                reader.GetString(0),
                Enum.TryParse<BridgeNotificationKind>(reader.GetString(1), ignoreCase: true, out var notificationKind)
                    ? notificationKind
                    : BridgeNotificationKind.CodexThreadCompleted,
                reader.GetString(2),
                reader.GetString(3),
                ReadDateTimeOffset(reader, 4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return results;
    }

    public async Task UpsertSessionsAsync(IEnumerable<CodexSessionSummaryDto> sessions, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var session in sessions)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                insert into howsitgoing_sessions (
                    id,
                    title,
                    source,
                    working_directory,
                    git_branch,
                    git_origin_url,
                    agent_nickname,
                    agent_role,
                    created_at,
                    updated_at,
                    archived,
                    status,
                    last_agent_message,
                    completed_at,
                    agent,
                    synced_at)
                values (
                    @id,
                    @title,
                    @source,
                    @working_directory,
                    @git_branch,
                    @git_origin_url,
                    @agent_nickname,
                    @agent_role,
                    @created_at,
                    @updated_at,
                    @archived,
                    @status,
                    @last_agent_message,
                    @completed_at,
                    @agent,
                    utc_timestamp(6))
                on duplicate key update
                    title = values(title),
                    source = values(source),
                    working_directory = values(working_directory),
                    git_branch = values(git_branch),
                    git_origin_url = values(git_origin_url),
                    agent_nickname = values(agent_nickname),
                    agent_role = values(agent_role),
                    created_at = values(created_at),
                    updated_at = values(updated_at),
                    archived = values(archived),
                    status = values(status),
                    last_agent_message = values(last_agent_message),
                    completed_at = values(completed_at),
                    agent = values(agent),
                    synced_at = values(synced_at);
                """;
            AddSessionParameters(command, session);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpsertNotificationsAsync(IEnumerable<BridgeNotificationDto> notifications, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var notification in notifications)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                insert into howsitgoing_notifications (
                    id,
                    kind,
                    title,
                    message,
                    occurred_at,
                    related_session_id,
                    related_repository,
                    related_url,
                    synced_at)
                values (
                    @id,
                    @kind,
                    @title,
                    @message,
                    @occurred_at,
                    @related_session_id,
                    @related_repository,
                    @related_url,
                    utc_timestamp(6))
                on duplicate key update
                    kind = values(kind),
                    title = values(title),
                    message = values(message),
                    occurred_at = values(occurred_at),
                    related_session_id = values(related_session_id),
                    related_repository = values(related_repository),
                    related_url = values(related_url),
                    synced_at = values(synced_at);
                """;
            AddNotificationParameters(command, notification);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<StartCodexRunResponse> QueueStartRunAndWaitAsync(
        StartCodexRunRequest request,
        string requestedBy,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("The shared store is not configured.");
        }

        var commandId = Guid.NewGuid().ToString("D");
        var now = DateTimeOffset.UtcNow;

        await using (var connection = await OpenConnectionAsync(cancellationToken))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                insert into howsitgoing_agent_commands (
                    id,
                    repo_path,
                    prompt,
                    model,
                    reasoning_effort,
                    allow_outside_git_repo,
                    requested_by,
                    status,
                    created_at,
                    updated_at)
                values (
                    @id,
                    @repo_path,
                    @prompt,
                    @model,
                    @reasoning_effort,
                    @allow_outside_git_repo,
                    @requested_by,
                    'pending',
                    @created_at,
                    @updated_at);
                """;
            command.Parameters.AddWithValue("@id", commandId);
            command.Parameters.AddWithValue("@repo_path", request.RepoPath.Trim());
            command.Parameters.AddWithValue("@prompt", request.Prompt.Trim());
            command.Parameters.AddWithValue("@model", CodexLaunchDefaults.ResolveModel(request.Model));
            command.Parameters.AddWithValue("@reasoning_effort", CodexLaunchDefaults.ResolveReasoningEffort(request.ReasoningEffort));
            command.Parameters.AddWithValue("@allow_outside_git_repo", request.AllowOutsideGitRepo ? 1 : 0);
            command.Parameters.AddWithValue("@requested_by", requestedBy);
            command.Parameters.AddWithValue("@created_at", now.UtcDateTime);
            command.Parameters.AddWithValue("@updated_at", now.UtcDateTime);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_options.CommandStartTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        while (!linked.IsCancellationRequested)
        {
            var status = await GetCommandStatusAsync(commandId, linked.Token);
            if (status is null)
            {
                break;
            }

            if (string.Equals(status.Status, "started", StringComparison.OrdinalIgnoreCase))
            {
                return new StartCodexRunResponse(
                    status.ThreadId,
                    status.StartedAt ?? DateTimeOffset.UtcNow,
                    request.RepoPath.Trim(),
                    CreatePromptPreview(request.Prompt),
                    string.IsNullOrWhiteSpace(status.LaunchMode) ? "shared-queue" : status.LaunchMode);
            }

            if (string.Equals(status.Status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(status.ErrorMessage)
                        ? "The shared bridge worker failed to start the Codex run."
                        : status.ErrorMessage);
            }

            await Task.Delay(TimeSpan.FromSeconds(2), linked.Token);
        }

        return new StartCodexRunResponse(
            null,
            now,
            request.RepoPath.Trim(),
            CreatePromptPreview(request.Prompt),
            "shared-queue-pending");
    }

    public async Task<SharedAgentCommand?> ClaimPendingCommandAsync(string processorId, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await RequeueExpiredCommandsAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        SharedAgentCommand? claimedCommand = null;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                select
                    id,
                    repo_path,
                    prompt,
                    model,
                    reasoning_effort,
                    allow_outside_git_repo
                from howsitgoing_agent_commands
                where status = 'pending'
                order by created_at
                limit 1
                for update;
                """;

            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                claimedCommand = new SharedAgentCommand(
                    reader.GetValue(0).ToString() ?? string.Empty,
                    new StartCodexRunRequest(
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetBoolean(5)));
            }
        }

        if (claimedCommand is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                update howsitgoing_agent_commands
                set
                    status = 'processing',
                    processor_id = @processor_id,
                    claimed_at = utc_timestamp(6),
                    updated_at = utc_timestamp(6),
                    error_message = null
                where id = @id;
                """;
            update.Parameters.AddWithValue("@processor_id", processorId);
            update.Parameters.AddWithValue("@id", claimedCommand.Id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return claimedCommand;
    }

    public async Task MarkCommandStartedAsync(string commandId, StartCodexRunResponse response, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update howsitgoing_agent_commands
            set
                status = 'started',
                result_thread_id = @result_thread_id,
                launch_mode = @launch_mode,
                started_at = @started_at,
                updated_at = utc_timestamp(6),
                error_message = null
            where id = @id;
            """;
        command.Parameters.AddWithValue("@result_thread_id", (object?)response.ThreadId ?? DBNull.Value);
        command.Parameters.AddWithValue("@launch_mode", response.LaunchMode);
        command.Parameters.AddWithValue("@started_at", response.StartedAt.UtcDateTime);
        command.Parameters.AddWithValue("@id", commandId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkCommandFailedAsync(string commandId, string errorMessage, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update howsitgoing_agent_commands
            set
                status = 'failed',
                error_message = @error_message,
                updated_at = utc_timestamp(6)
            where id = @id;
            """;
        command.Parameters.AddWithValue("@error_message", errorMessage);
        command.Parameters.AddWithValue("@id", commandId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SharedAgentCommandStatus?> GetCommandStatusAsync(string commandId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select
                status,
                error_message,
                result_thread_id,
                launch_mode,
                started_at
            from howsitgoing_agent_commands
            where id = @id;
            """;
        command.Parameters.AddWithValue("@id", commandId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SharedAgentCommandStatus(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : ReadDateTimeOffset(reader, 4));
    }

    private async Task<MySqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("The shared store is not configured.");
        }

        var connection = new MySqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        return connection;
    }

    private async Task EnsureSchemaAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        if (_schemaEnsured)
        {
            return;
        }

        await _schemaGate.WaitAsync(cancellationToken);
        try
        {
            if (_schemaEnsured)
            {
                return;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = """
                create table if not exists howsitgoing_sessions (
                    id varchar(64) not null primary key,
                    title text not null,
                    source varchar(512) not null,
                    working_directory text not null,
                    git_branch varchar(255) null,
                    git_origin_url text null,
                    agent_nickname varchar(255) null,
                    agent_role varchar(255) null,
                    created_at datetime(6) not null,
                    updated_at datetime(6) not null,
                    archived tinyint(1) not null,
                    status varchar(32) not null,
                    last_agent_message mediumtext null,
                    completed_at datetime(6) null,
                    agent varchar(32) not null default 'codex',
                    synced_at datetime(6) not null,
                    index idx_howsitgoing_sessions_updated (updated_at),
                    index idx_howsitgoing_sessions_source (source),
                    index idx_howsitgoing_sessions_status (status),
                    index idx_howsitgoing_sessions_agent (agent)
                ) character set utf8mb4 collate utf8mb4_unicode_ci;

                create table if not exists howsitgoing_notifications (
                    id varchar(128) not null primary key,
                    kind varchar(64) not null,
                    title text not null,
                    message mediumtext not null,
                    occurred_at datetime(6) not null,
                    related_session_id varchar(64) null,
                    related_repository text null,
                    related_url text null,
                    synced_at datetime(6) not null,
                    index idx_howsitgoing_notifications_occurred (occurred_at)
                ) character set utf8mb4 collate utf8mb4_unicode_ci;

                create table if not exists howsitgoing_agent_commands (
                    id char(36) not null primary key,
                    repo_path text not null,
                    prompt mediumtext not null,
                    model varchar(128) not null,
                    reasoning_effort varchar(64) not null,
                    allow_outside_git_repo tinyint(1) not null,
                    requested_by varchar(255) not null,
                    status varchar(32) not null,
                    error_message text null,
                    result_thread_id varchar(64) null,
                    launch_mode text null,
                    created_at datetime(6) not null,
                    updated_at datetime(6) not null,
                    claimed_at datetime(6) null,
                    started_at datetime(6) null,
                    processor_id varchar(255) null,
                    index idx_howsitgoing_agent_commands_status_created (status, created_at),
                    index idx_howsitgoing_agent_commands_processor (processor_id, status, claimed_at)
                ) character set utf8mb4 collate utf8mb4_unicode_ci;

                alter table howsitgoing_sessions modify column source varchar(512) not null;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);

            // MySQL has no "add column if not exists"; ignore the duplicate-column error on existing tables.
            await using (var addAgentColumn = connection.CreateCommand())
            {
                addAgentColumn.CommandText =
                    "alter table howsitgoing_sessions add column agent varchar(32) not null default 'codex';";
                try
                {
                    await addAgentColumn.ExecuteNonQueryAsync(cancellationToken);
                }
                catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateFieldName)
                {
                }
            }

            _schemaEnsured = true;
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    private static void AddSessionParameters(MySqlCommand command, CodexSessionSummaryDto session)
    {
        command.Parameters.AddWithValue("@id", session.Id);
        command.Parameters.AddWithValue("@title", session.Title);
        command.Parameters.AddWithValue("@source", session.Source);
        command.Parameters.AddWithValue("@working_directory", session.WorkingDirectory);
        command.Parameters.AddWithValue("@git_branch", (object?)session.GitBranch ?? DBNull.Value);
        command.Parameters.AddWithValue("@git_origin_url", (object?)session.GitOriginUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("@agent_nickname", (object?)session.AgentNickname ?? DBNull.Value);
        command.Parameters.AddWithValue("@agent_role", (object?)session.AgentRole ?? DBNull.Value);
        command.Parameters.AddWithValue("@created_at", session.CreatedAt.UtcDateTime);
        command.Parameters.AddWithValue("@updated_at", session.UpdatedAt.UtcDateTime);
        command.Parameters.AddWithValue("@archived", session.Archived ? 1 : 0);
        command.Parameters.AddWithValue("@status", session.Status.ToString());
        command.Parameters.AddWithValue("@last_agent_message", (object?)session.LastAgentMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("@completed_at", session.CompletedAt is { } completedAt
            ? completedAt.UtcDateTime
            : DBNull.Value);
        command.Parameters.AddWithValue("@agent", string.IsNullOrWhiteSpace(session.Agent) ? AgentKinds.Codex : session.Agent);
    }

    private static void AddNotificationParameters(MySqlCommand command, BridgeNotificationDto notification)
    {
        command.Parameters.AddWithValue("@id", notification.Id);
        command.Parameters.AddWithValue("@kind", notification.Kind.ToString());
        command.Parameters.AddWithValue("@title", notification.Title);
        command.Parameters.AddWithValue("@message", notification.Message);
        command.Parameters.AddWithValue("@occurred_at", notification.OccurredAt.UtcDateTime);
        command.Parameters.AddWithValue("@related_session_id", (object?)notification.RelatedSessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("@related_repository", (object?)notification.RelatedRepository ?? DBNull.Value);
        command.Parameters.AddWithValue("@related_url", (object?)notification.RelatedUrl ?? DBNull.Value);
    }

    private static DateTimeOffset ReadDateTimeOffset(MySqlDataReader reader, int ordinal)
    {
        var value = reader.GetDateTime(ordinal);
        var utc = value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return new DateTimeOffset(utc);
    }

    private static string CreatePromptPreview(string prompt)
    {
        var trimmed = prompt.Trim();
        return trimmed.Length > 120 ? trimmed[..120] + "..." : trimmed;
    }

    private static async Task RequeueExpiredCommandsAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update howsitgoing_agent_commands
            set
                status = 'pending',
                processor_id = null,
                claimed_at = null,
                updated_at = utc_timestamp(6)
            where status = 'processing'
              and claimed_at < utc_timestamp(6) - interval 5 minute;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record SharedAgentCommandStatus(
        string Status,
        string? ErrorMessage,
        string? ThreadId,
        string? LaunchMode,
        DateTimeOffset? StartedAt);
}

public sealed record SharedAgentCommand(string Id, StartCodexRunRequest Request);
