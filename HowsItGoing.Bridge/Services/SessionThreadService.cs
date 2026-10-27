using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HowsItGoing.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace HowsItGoing.Bridge.Services;

/// <summary>
/// Reads a full session transcript on demand for the app's thread viewer.
///
/// The feed only carries the last agent message, which is enough to scan but not enough to read a
/// run. This service walks the same three stores the feed reads (Codex rollout JSONL, Claude Code
/// project JSONL, OpenCode sqlite) and returns one window of readable turns at a time, newest first.
/// It is deliberately read-only and on-demand: a Codex rollout can be 25 MB, so nothing here runs
/// during a refresh tick.
/// </summary>
public sealed class SessionThreadService
{
    /// <summary>
    /// One turn is clamped to this many characters. A single Codex or Claude turn can embed a whole
    /// file, and an uncapped window is what makes a phone transcript view stutter.
    /// </summary>
    private const int MaxMessageCharacters = 20_000;

    private const int MaxToolNamesPerRow = 4;

    private static readonly Regex InjectedContextBlock = new(
        @"<(environment_context|user_instructions|system-reminder|task-notification|oai-mem-citation)>.*?</\1>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex LeadingBracketContext = new(
        @"^\s*<(environment_context|user_instructions|task-notification)>.*?</\1>\s*",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ToolRunPrefix = new(@"^Ran\s+", RegexOptions.Compiled);

    private static readonly Regex ToolRunSuffix = new(@"\s*\(\+\d+ more\)\s*$", RegexOptions.Compiled);

    private readonly string _codexStateDbPath;
    private readonly string _claudeProjectsRoot;
    private readonly string _openCodeDbPath;
    private readonly ILogger<SessionThreadService> _logger;

    public SessionThreadService(IConfiguration configuration, ILogger<SessionThreadService> logger)
    {
        var codexHome = BridgeOptions.ResolveCodexHome(configuration);
        _codexStateDbPath = configuration["Bridge:StateDbPath"] ?? Path.Combine(codexHome, "state_5.sqlite");

        var configuredClaudeHome = configuration["Bridge:ClaudeHome"];
        var claudeHome = string.IsNullOrWhiteSpace(configuredClaudeHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : Environment.ExpandEnvironmentVariables(configuredClaudeHome);
        _claudeProjectsRoot = Path.Combine(claudeHome, "projects");

        var configuredOpenCode = configuration["Bridge:OpenCodeDbPath"];
        _openCodeDbPath = string.IsNullOrWhiteSpace(configuredOpenCode)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "opencode", "opencode.db")
            : Environment.ExpandEnvironmentVariables(configuredOpenCode);

        _logger = logger;
    }

    public async Task<SessionThreadDto?> GetThreadAsync(
        string agent,
        string sessionId,
        int limit,
        int? before,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var normalizedAgent = AgentKinds.Normalize(agent) ?? AgentKinds.Codex;
        try
        {
            return normalizedAgent switch
            {
                AgentKinds.ClaudeCode => await ReadClaudeThreadAsync(sessionId.Trim(), limit, before, cancellationToken),
                AgentKinds.OpenCode => await ReadOpenCodeThreadAsync(sessionId.Trim(), limit, before, cancellationToken),
                _ => await ReadCodexThreadAsync(sessionId.Trim(), limit, before, cancellationToken)
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read the {Agent} transcript for {SessionId}.", normalizedAgent, sessionId);
            return null;
        }
    }

    // ---- Codex ------------------------------------------------------------------------------

    private async Task<SessionThreadDto?> ReadCodexThreadAsync(
        string sessionId,
        int limit,
        int? before,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_codexStateDbPath))
        {
            return null;
        }

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _codexStateDbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);

        string? rolloutPath = null;
        string? title = null;
        string? cwd = null;
        string? gitBranch = null;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                select rollout_path, title, cwd, git_branch
                from threads
                where id = @id
                limit 1;
                """;
            command.Parameters.AddWithValue("@id", sessionId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            rolloutPath = reader.IsDBNull(0) ? null : reader.GetString(0);
            title = reader.IsDBNull(1) ? null : reader.GetString(1);
            cwd = reader.IsDBNull(2) ? null : reader.GetString(2);
            gitBranch = reader.IsDBNull(3) ? null : reader.GetString(3);
        }

        var path = NormalizePath(rolloutPath);
        var messages = path is not null && File.Exists(path)
            ? await ParseCodexRolloutAsync(path, cancellationToken)
            : [];

        return BuildThread(AgentKinds.Codex, sessionId, title ?? sessionId, cwd, gitBranch, messages, limit, before);
    }

    private static async Task<List<SessionThreadMessageDto>> ParseCodexRolloutAsync(
        string rolloutPath,
        CancellationToken cancellationToken)
    {
        var messages = new List<SessionThreadMessageDto>();

        await using var stream = new FileStream(
            rolloutPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("type", out var typeValue) ||
                    typeValue.ValueKind != JsonValueKind.String ||
                    !root.TryGetProperty("payload", out var payload) ||
                    payload.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var timestamp = ReadTimestamp(root);
                var entryType = typeValue.GetString();

                if (entryType == "response_item")
                {
                    if (ReadString(payload, "type") != "message")
                    {
                        continue;
                    }

                    var role = ReadString(payload, "role");
                    var messageRole = role switch
                    {
                        "user" => SessionMessageRole.User,
                        "assistant" => SessionMessageRole.Assistant,
                        _ => (SessionMessageRole?)null
                    };

                    if (messageRole is null)
                    {
                        // "developer" and "system" rows are injected instructions, not conversation.
                        continue;
                    }

                    var text = ExtractTextParts(payload, messageRole == SessionMessageRole.User ? "input_text" : "output_text");
                    if (messageRole == SessionMessageRole.User)
                    {
                        text = StripInjectedContext(text);
                    }

                    AddMessage(messages, messageRole.Value, text, timestamp);
                }
                else if (entryType == "event_msg")
                {
                    // Older rollouts record the visible turns as events instead of response items.
                    switch (ReadString(payload, "type"))
                    {
                        case "user_message":
                            AddMessage(messages, SessionMessageRole.User, StripInjectedContext(ReadString(payload, "message")), timestamp);
                            break;
                        case "agent_message":
                            AddMessage(messages, SessionMessageRole.Assistant, ReadString(payload, "message"), timestamp);
                            break;
                    }
                }
            }
            catch (JsonException)
            {
                // Codex exec writes warning lines before the JSON; a truncated tail line is normal.
            }
        }

        return messages;
    }

    // ---- Claude Code ------------------------------------------------------------------------

    private async Task<SessionThreadDto?> ReadClaudeThreadAsync(
        string sessionId,
        int limit,
        int? before,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_claudeProjectsRoot))
        {
            return null;
        }

        var transcript = FindClaudeTranscript(sessionId);
        if (transcript is null)
        {
            return null;
        }

        var messages = new List<SessionThreadMessageDto>();
        string? title = null;
        string? cwd = null;
        string? gitBranch = null;

        await using (var stream = new FileStream(
            transcript,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    title ??= ReadString(root, "aiTitle") ?? ReadString(root, "customTitle");
                    cwd ??= ReadString(root, "cwd");
                    gitBranch ??= ReadString(root, "gitBranch");

                    var entryType = ReadString(root, "type");
                    if (entryType is not ("user" or "assistant") ||
                        !root.TryGetProperty("message", out var message) ||
                        message.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var isMeta = root.TryGetProperty("isMeta", out var metaValue) && metaValue.ValueKind == JsonValueKind.True;
                    if (isMeta)
                    {
                        continue;
                    }

                    var role = entryType == "user" ? SessionMessageRole.User : SessionMessageRole.Assistant;
                    var turn = ExtractClaudeText(message);
                    var text = turn.Text;
                    if (role == SessionMessageRole.User)
                    {
                        text = StripInjectedContext(text);
                    }

                    AddMessage(messages, turn.IsTool ? SessionMessageRole.Tool : role, text, ReadTimestamp(root));
                }
                catch (JsonException)
                {
                    // Skip malformed lines.
                }
            }
        }

        return BuildThread(
            AgentKinds.ClaudeCode,
            sessionId,
            title ?? sessionId,
            cwd,
            gitBranch,
            messages,
            limit,
            before);
    }

    private string? FindClaudeTranscript(string sessionId)
    {
        var fileName = sessionId + ".jsonl";
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(_claudeProjectsRoot))
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to look up a Claude Code transcript for {SessionId}.", sessionId);
        }

        return null;
    }

    private static ParsedTurn ExtractClaudeText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
        {
            return default;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return new ParsedTurn(content.GetString(), false);
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return default;
        }

        var texts = new List<string>();
        var toolNames = new List<string>();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var partType = ReadString(part, "type");
            if (partType == "text" && ReadString(part, "text") is { } text && !string.IsNullOrWhiteSpace(text))
            {
                texts.Add(text);
            }
            else if (partType == "tool_use" && ReadString(part, "name") is { } toolName)
            {
                toolNames.Add(toolName);
            }
        }

        if (texts.Count > 0)
        {
            return new ParsedTurn(string.Join("\n\n", texts), false);
        }

        return toolNames.Count > 0 ? new ParsedTurn(DescribeTools(toolNames), true) : default;
    }

    // ---- OpenCode ---------------------------------------------------------------------------

    private async Task<SessionThreadDto?> ReadOpenCodeThreadAsync(
        string sessionId,
        int limit,
        int? before,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_openCodeDbPath))
        {
            return null;
        }

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _openCodeDbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);

        var sessionsTable = await TableExistsAsync(connection, "session_v2", cancellationToken)
            ? "session_v2"
            : "session";

        string? title = null;
        string? directory = null;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"select title, directory from {sessionsTable} where id = @id limit 1;";
            command.Parameters.AddWithValue("@id", sessionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            title = reader.IsDBNull(0) ? null : reader.GetString(0);
            directory = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        var messages = sessionsTable == "session_v2"
            ? await ReadOpenCodeV2MessagesAsync(connection, sessionId, cancellationToken)
            : await ReadOpenCodeV1MessagesAsync(connection, sessionId, cancellationToken);

        return BuildThread(AgentKinds.OpenCode, sessionId, title ?? sessionId, directory, null, messages, limit, before);
    }

    private static async Task<List<SessionThreadMessageDto>> ReadOpenCodeV2MessagesAsync(
        SqliteConnection connection,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var messages = new List<SessionThreadMessageDto>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select id, type, time_created, data
            from session_message
            where session_id = @sessionId
              and type in ('user', 'assistant')
            order by time_created asc, id asc;
            """;
        command.Parameters.AddWithValue("@sessionId", sessionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            var type = reader.GetString(1);
            var createdAt = reader.IsDBNull(2)
                ? (DateTimeOffset?)null
                : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2));
            var data = reader.IsDBNull(3) ? null : reader.GetString(3);

            var role = type == "user" ? SessionMessageRole.User : SessionMessageRole.Assistant;
            var turn = ExtractOpenCodeText(data);
            AddMessage(messages, turn.IsTool ? SessionMessageRole.Tool : role, turn.Text, createdAt, id);
        }

        return messages;
    }

    private static async Task<List<SessionThreadMessageDto>> ReadOpenCodeV1MessagesAsync(
        SqliteConnection connection,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var messages = new List<SessionThreadMessageDto>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select p.id, json_extract(m.data, '$.role'), p.data, json_extract(m.data, '$.time.created')
            from part p
            join message m on m.id = p.message_id
            where m.session_id = @sessionId
              and json_extract(m.data, '$.role') in ('user', 'assistant')
            order by p.id asc;
            """;
        command.Parameters.AddWithValue("@sessionId", sessionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            var roleText = reader.IsDBNull(1) ? null : reader.GetString(1);
            var data = reader.IsDBNull(2) ? null : reader.GetString(2);
            DateTimeOffset? createdAt = null;
            if (!reader.IsDBNull(3))
            {
                var raw = reader.GetInt64(3);
                createdAt = raw > 100_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(raw)
                    : DateTimeOffset.FromUnixTimeSeconds(raw);
            }

            var role = roleText == "user" ? SessionMessageRole.User : SessionMessageRole.Assistant;
            var turn = ExtractOpenCodeText(data);
            AddMessage(messages, turn.IsTool ? SessionMessageRole.Tool : role, turn.Text, createdAt, id);
        }

        return messages;
    }

    private static ParsedTurn ExtractOpenCodeText(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return default;
        }

        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                return new ParsedTurn(ReadString(root, "text"), false);
            }

            var texts = new List<string>();
            var toolNames = new List<string>();
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var partType = ReadString(part, "type");
                if (partType == "text" && ReadString(part, "text") is { } text && !string.IsNullOrWhiteSpace(text))
                {
                    texts.Add(text);
                }
                else if (partType == "tool" && ReadString(part, "name") is { } toolName)
                {
                    toolNames.Add(toolName);
                }
            }

            if (texts.Count > 0)
            {
                return new ParsedTurn(string.Join("\n\n", texts), false);
            }

            return toolNames.Count > 0 ? new ParsedTurn(DescribeTools(toolNames), true) : default;
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary>One extracted turn: the readable text plus whether it is a tool summary.</summary>
    private readonly record struct ParsedTurn(string? Text, bool IsTool);

    // ---- Shared -----------------------------------------------------------------------------

    private static SessionThreadDto BuildThread(
        string agent,
        string sessionId,
        string title,
        string? workingDirectory,
        string? gitBranch,
        List<SessionThreadMessageDto> messages,
        int limit,
        int? before)
    {
        CollapseToolRuns(messages);

        var (start, count) = SessionThreadPaging.SelectWindow(messages.Count, limit, before);
        var window = messages.Skip(start).Take(count).ToArray();

        return new SessionThreadDto(
            agent,
            sessionId,
            title,
            workingDirectory,
            gitBranch,
            window,
            messages.Count,
            start,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// A tool-heavy run writes one row per tool call, so a raw transcript is mostly "Ran shell"
    /// repeated. Consecutive tool rows become one line naming what ran, which keeps the shape of
    /// the work visible without burying the conversation.
    /// </summary>
    private static void CollapseToolRuns(List<SessionThreadMessageDto> messages)
    {
        for (var i = messages.Count - 1; i > 0; i--)
        {
            if (messages[i].Role != SessionMessageRole.Tool || messages[i - 1].Role != SessionMessageRole.Tool)
            {
                continue;
            }

            var names = ReadToolNames(messages[i - 1].Text)
                .Concat(ReadToolNames(messages[i].Text))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            messages[i - 1] = messages[i - 1] with { Text = FormatToolRun(names) };
            messages.RemoveAt(i);
        }
    }

    private static IEnumerable<string> ReadToolNames(string text) =>
        ToolRunSuffix
            .Replace(ToolRunPrefix.Replace(text, string.Empty), string.Empty)
            .Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string FormatToolRun(IReadOnlyList<string> names)
    {
        if (names.Count == 0)
        {
            return "Ran tools";
        }

        var shown = names.Take(MaxToolNamesPerRow).ToArray();
        var suffix = names.Count > shown.Length ? $" (+{names.Count - shown.Length} more)" : string.Empty;
        return $"Ran {string.Join(", ", shown)}{suffix}";
    }

    private static void AddMessage(
        List<SessionThreadMessageDto> messages,
        SessionMessageRole role,
        string? text,
        DateTimeOffset? createdAt,
        string? id = null)
    {
        var cleaned = Clean(text);
        if (cleaned is null)
        {
            return;
        }

        // Injected blocks (system reminders, memory citations, environment context) can arrive on
        // any role, including the model echoing a heartbeat back.
        cleaned = StripInjectedContext(cleaned);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return;
        }

        // Automation runs make the model answer a heartbeat with the heartbeat XML itself; that is
        // not a turn anyone wants to read in a transcript.
        if (cleaned.StartsWith("<heartbeat>", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (role == SessionMessageRole.User && IsInjectedUserMessage(cleaned))
        {
            return;
        }

        // The same turn is recorded twice in some stores (a response item plus its event), and a
        // duplicated bubble reads like the agent answered twice.
        if (messages.Count > 0)
        {
            var last = messages[^1];
            if (last.Role == role && string.Equals(last.Text, cleaned, StringComparison.Ordinal))
            {
                return;
            }
        }

        messages.Add(new SessionThreadMessageDto(
            id ?? $"m{messages.Count}",
            role,
            cleaned,
            createdAt));
    }

    /// <summary>
    /// Agents inject machine turns into their own transcript (heartbeats, environment context,
    /// instruction preambles). They are not conversation and they dominate a rollout, so the
    /// transcript reader drops them instead of making the user scroll past them.
    /// </summary>
    private static bool IsInjectedUserMessage(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith("<heartbeat>", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<environment_context>", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<user_instructions>", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<turn_aborted>", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<external_codex_apps_", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("# AGENTS.md instructions", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<INSTRUCTIONS>", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.ReplaceLineEndings("\n").Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        return trimmed.Length <= MaxMessageCharacters
            ? trimmed
            : trimmed[..MaxMessageCharacters] + "\n\n[message truncated]";
    }

    private static string? StripInjectedContext(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var stripped = LeadingBracketContext.Replace(text, string.Empty);
        if (ReferenceEquals(stripped, text))
        {
            stripped = InjectedContextBlock.Replace(text, string.Empty);
        }

        return stripped.Trim();
    }

    private static string DescribeTools(IReadOnlyList<string> toolNames)
    {
        var distinct = toolNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxToolNamesPerRow)
            .ToArray();

        if (distinct.Length == 0)
        {
            return string.Empty;
        }

        var suffix = toolNames.Count > distinct.Length ? $" (+{toolNames.Count - distinct.Length} more)" : string.Empty;
        return $"Ran {string.Join(", ", distinct)}{suffix}";
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

    private static string? NormalizePath(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? null
            : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;

    private static string? ExtractTextParts(JsonElement payload, string partType)
    {
        if (!payload.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var texts = new List<string>();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object &&
                ReadString(part, "type") == partType &&
                ReadString(part, "text") is { } text &&
                !string.IsNullOrWhiteSpace(text))
            {
                texts.Add(text);
            }
        }

        return texts.Count == 0 ? null : string.Join("\n\n", texts);
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement element)
    {
        if (!element.TryGetProperty("timestamp", out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value.GetString(),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal,
            out var timestamp)
            ? timestamp
            : null;
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
