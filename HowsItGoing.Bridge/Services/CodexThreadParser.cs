using System.Globalization;
using System.Text.Json;
using HowsItGoing.Contracts;
using Microsoft.Extensions.Options;

namespace HowsItGoing.Bridge.Services;

public sealed class CodexThreadParser
{
    private readonly int _runningThresholdSeconds;
    private readonly Dictionary<string, CachedRuntimeState> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheLock = new();

    public CodexThreadParser(IOptions<BridgeOptions> options)
    {
        _runningThresholdSeconds = options.Value.RunningThresholdSeconds;
    }

    public async Task<CodexRuntimeState> GetRuntimeStateAsync(string? rolloutPath, DateTimeOffset updatedAt, bool archived, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rolloutPath) || !File.Exists(rolloutPath))
        {
            return new CodexRuntimeState(ComputeStatus(archived, false, updatedAt), null, null);
        }

        var info = new FileInfo(rolloutPath);
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(rolloutPath, out var cached) &&
                cached.Length == info.Length &&
                cached.LastWriteUtc == info.LastWriteTimeUtc)
            {
                return cached.State;
            }
        }

        string? lastAgentMessage = null;
        DateTimeOffset? completedAt = null;
        var hasTaskComplete = false;

        await using var stream = File.OpenRead(rolloutPath);
        using var reader = new StreamReader(stream);
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
                if (!root.TryGetProperty("type", out var rootType) ||
                    rootType.ValueKind != JsonValueKind.String ||
                    !root.TryGetProperty("payload", out var payload))
                {
                    continue;
                }

                var entryType = rootType.GetString();
                if (entryType == "event_msg" &&
                    payload.TryGetProperty("type", out var payloadType) &&
                    payloadType.ValueKind == JsonValueKind.String)
                {
                    var payloadTypeValue = payloadType.GetString();
                    if (payloadTypeValue == "agent_message" &&
                        payload.TryGetProperty("message", out var messageValue) &&
                        messageValue.ValueKind == JsonValueKind.String)
                    {
                        lastAgentMessage = messageValue.GetString();
                    }
                    else if (payloadTypeValue == "task_complete")
                    {
                        hasTaskComplete = true;
                        completedAt = TryReadTimestamp(root);
                        if (payload.TryGetProperty("last_agent_message", out var lastAgentMessageValue) &&
                            lastAgentMessageValue.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(lastAgentMessageValue.GetString()))
                        {
                            lastAgentMessage = lastAgentMessageValue.GetString();
                        }
                    }
                }
                else if (entryType == "response_item" &&
                         payload.TryGetProperty("type", out var responseType) &&
                         responseType.ValueKind == JsonValueKind.String &&
                         responseType.GetString() == "message" &&
                         payload.TryGetProperty("role", out var roleValue) &&
                         roleValue.ValueKind == JsonValueKind.String &&
                         roleValue.GetString() == "assistant")
                {
                    var assistantText = TryExtractAssistantText(payload);
                    if (!string.IsNullOrWhiteSpace(assistantText))
                    {
                        lastAgentMessage = assistantText;
                    }
                }
            }
            catch (JsonException)
            {
                // Codex exec emits warning lines before JSON lines.
            }
        }

        var state = new CodexRuntimeState(ComputeStatus(archived, hasTaskComplete, updatedAt), lastAgentMessage, completedAt);
        lock (_cacheLock)
        {
            _cache[rolloutPath] = new CachedRuntimeState(info.Length, info.LastWriteTimeUtc, state);
        }

        return state;
    }

    private static string? TryExtractAssistantText(JsonElement payload)
    {
        if (!payload.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in content.EnumerateArray().Reverse())
        {
            if (item.TryGetProperty("type", out var typeValue) &&
                typeValue.ValueKind == JsonValueKind.String &&
                typeValue.GetString() == "output_text" &&
                item.TryGetProperty("text", out var textValue) &&
                textValue.ValueKind == JsonValueKind.String)
            {
                return textValue.GetString();
            }
        }

        return null;
    }

    private static DateTimeOffset? TryReadTimestamp(JsonElement root)
    {
        if (!root.TryGetProperty("timestamp", out var timestampValue) || timestampValue.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(timestampValue.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp)
            ? timestamp
            : null;
    }

    private CodexSessionStatus ComputeStatus(bool archived, bool hasTaskComplete, DateTimeOffset updatedAt)
    {
        if (archived)
        {
            return CodexSessionStatus.Archived;
        }

        if (hasTaskComplete)
        {
            return CodexSessionStatus.Completed;
        }

        return DateTimeOffset.UtcNow - updatedAt <= TimeSpan.FromSeconds(_runningThresholdSeconds)
            ? CodexSessionStatus.Running
            : CodexSessionStatus.Idle;
    }

    private sealed record CachedRuntimeState(long Length, DateTime LastWriteUtc, CodexRuntimeState State);
}

public sealed record CodexRuntimeState(CodexSessionStatus Status, string? LastAgentMessage, DateTimeOffset? CompletedAt);
