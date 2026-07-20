using System.Text;
using System.Text.Json;
using HowsItGoing.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace HowsItGoing.Bridge.Services;

/// <summary>
/// Reads Claude Code session transcripts from ~/.claude/projects/&lt;cwd-slug&gt;/&lt;session-id&gt;.jsonl.
/// </summary>
public sealed class ClaudeCodeSessionService
{
    private const int HeadLinesToScan = 40;
    private const int TailBytesToScan = 192 * 1024;

    private readonly string _projectsRoot;
    private readonly int _runningThresholdSeconds;
    private readonly int _maxSessions;
    private readonly ILogger<ClaudeCodeSessionService> _logger;
    private readonly Dictionary<string, CachedSession> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheLock = new();

    public ClaudeCodeSessionService(
        IConfiguration configuration,
        IOptions<BridgeOptions> options,
        ILogger<ClaudeCodeSessionService> logger)
    {
        var configured = configuration["Bridge:ClaudeHome"];
        var claudeHome = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : Environment.ExpandEnvironmentVariables(configured);
        _projectsRoot = Path.Combine(claudeHome, "projects");
        _runningThresholdSeconds = options.Value.RunningThresholdSeconds;
        _maxSessions = options.Value.MaxSessionsPerAgent;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CodexSessionSummaryDto>> GetSessionsAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_projectsRoot))
        {
            return [];
        }

        List<FileInfo> transcripts;
        try
        {
            transcripts = Directory.EnumerateDirectories(_projectsRoot)
                .SelectMany(dir => Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.TopDirectoryOnly))
                .Select(path => new FileInfo(path))
                .Where(info => info.Length > 0)
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .Take(_maxSessions)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to enumerate Claude Code transcripts under {Root}.", _projectsRoot);
            return [];
        }

        var results = new List<CodexSessionSummaryDto>(transcripts.Count);
        foreach (var transcript in transcripts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var session = await ParseTranscriptAsync(transcript, cancellationToken);
                if (session is not null)
                {
                    results.Add(session);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse Claude Code transcript {Path}.", transcript.FullName);
            }
        }

        return results;
    }

    private async Task<CodexSessionSummaryDto?> ParseTranscriptAsync(FileInfo transcript, CancellationToken cancellationToken)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(transcript.FullName, out var cached) &&
                cached.Length == transcript.Length &&
                cached.LastWriteUtc == transcript.LastWriteTimeUtc)
            {
                return WithCurrentStatus(cached.Session, transcript);
            }
        }

        var sessionId = Path.GetFileNameWithoutExtension(transcript.Name);
        string? title = null;
        string? cwd = null;
        string? gitBranch = null;
        string? firstUserText = null;
        DateTimeOffset? createdAt = null;

        await using (var stream = OpenShared(transcript.FullName))
        using (var reader = new StreamReader(stream))
        {
            for (var i = 0; i < HeadLinesToScan && !reader.EndOfStream; i++)
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

                    title ??= ReadString(root, "aiTitle");
                    cwd ??= ReadString(root, "cwd");
                    gitBranch ??= ReadString(root, "gitBranch");
                    if (createdAt is null &&
                        ReadString(root, "timestamp") is { } timestamp &&
                        DateTimeOffset.TryParse(timestamp, out var parsedTimestamp))
                    {
                        createdAt = parsedTimestamp;
                    }

                    if (firstUserText is null &&
                        ReadString(root, "type") == "user" &&
                        root.TryGetProperty("message", out var message))
                    {
                        firstUserText = ExtractMessageText(message);
                    }

                    if (title is not null && cwd is not null && gitBranch is not null && firstUserText is not null)
                    {
                        break;
                    }
                }
                catch (JsonException)
                {
                    // Skip malformed lines.
                }
            }
        }

        var lastAssistantMessage = await ReadLastAssistantMessageAsync(transcript, cancellationToken);

        title ??= Truncate(firstUserText, 80) ?? sessionId;
        var session = new CodexSessionSummaryDto(
            sessionId,
            title,
            "claude-code",
            cwd ?? DecodeProjectSlug(transcript.Directory?.Name),
            gitBranch,
            GitOriginUrl: null,
            AgentNickname: null,
            AgentRole: null,
            createdAt ?? transcript.CreationTimeUtc,
            new DateTimeOffset(transcript.LastWriteTimeUtc, TimeSpan.Zero),
            Archived: false,
            CodexSessionStatus.Idle,
            lastAssistantMessage,
            CompletedAt: null,
            AgentKinds.ClaudeCode);

        lock (_cacheLock)
        {
            _cache[transcript.FullName] = new CachedSession(transcript.Length, transcript.LastWriteTimeUtc, session);
        }

        return WithCurrentStatus(session, transcript);
    }

    private CodexSessionSummaryDto WithCurrentStatus(CodexSessionSummaryDto session, FileInfo transcript)
    {
        var status = DateTimeOffset.UtcNow - transcript.LastWriteTimeUtc <= TimeSpan.FromSeconds(_runningThresholdSeconds)
            ? CodexSessionStatus.Running
            : CodexSessionStatus.Idle;
        return session with { Status = status };
    }

    private static async Task<string?> ReadLastAssistantMessageAsync(FileInfo transcript, CancellationToken cancellationToken)
    {
        await using var stream = OpenShared(transcript.FullName);
        if (stream.Length > TailBytesToScan)
        {
            stream.Seek(-TailBytesToScan, SeekOrigin.End);
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        if (stream.Position > 0)
        {
            // Discard the partial line at the seek boundary.
            await reader.ReadLineAsync(cancellationToken);
        }

        string? lastAssistantText = null;
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
                    ReadString(root, "type") != "assistant" ||
                    !root.TryGetProperty("message", out var message))
                {
                    continue;
                }

                var text = ExtractMessageText(message);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    lastAssistantText = text;
                }
            }
            catch (JsonException)
            {
                // Skip malformed or truncated lines.
            }
        }

        return lastAssistantText;
    }

    private static string? ExtractMessageText(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("content", out var content))
        {
            return null;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            var text = content.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var part in content.EnumerateArray().Reverse())
        {
            if (part.ValueKind == JsonValueKind.Object &&
                ReadString(part, "type") == "text" &&
                ReadString(part, "text") is { } text &&
                !string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static string DecodeProjectSlug(string? slug) =>
        string.IsNullOrWhiteSpace(slug) ? string.Empty : slug.Replace("--", @":\", StringComparison.Ordinal).Replace('-', '\\');

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var collapsed = value.ReplaceLineEndings(" ").Trim();
        return collapsed.Length <= maxLength ? collapsed : collapsed[..maxLength] + "…";
    }

    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private sealed record CachedSession(long Length, DateTime LastWriteUtc, CodexSessionSummaryDto Session);
}
