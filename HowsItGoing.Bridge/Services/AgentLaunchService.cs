using System.Diagnostics;
using System.Text.Json;
using HowsItGoing.Bridge.State;
using HowsItGoing.Contracts;

namespace HowsItGoing.Bridge.Services;

public sealed class AgentLaunchService
{
    private readonly BridgeStateStore _stateStore;
    private readonly ILogger<AgentLaunchService> _logger;

    public AgentLaunchService(BridgeStateStore stateStore, ILogger<AgentLaunchService> logger)
    {
        _stateStore = stateStore;
        _logger = logger;
    }

    public async Task<StartCodexRunResponse> StartAsync(StartCodexRunRequest request, CancellationToken cancellationToken)
    {
        var repoPath = Path.GetFullPath(request.RepoPath);
        if (!Directory.Exists(repoPath))
        {
            throw new DirectoryNotFoundException($"Repo path does not exist: {repoPath}");
        }

        var arguments = new List<string>
        {
            "exec",
            "--json",
            "--full-auto",
            "-C",
            repoPath
        };

        if (!Directory.Exists(Path.Combine(repoPath, ".git")) && request.AllowOutsideGitRepo)
        {
            arguments.Add("--skip-git-repo-check");
        }

        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            arguments.Add("--model");
            arguments.Add(request.Model);
        }

        arguments.Add(request.Prompt);

        var startInfo = CreateStartInfo(repoPath, arguments);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var threadStarted = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        process.Start();
        _ = MonitorProcessAsync(process, repoPath, threadStarted, cancellationToken);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var registration = linked.Token.Register(() => threadStarted.TrySetCanceled(linked.Token));

        try
        {
            var threadId = await threadStarted.Task;
            return new StartCodexRunResponse(
                threadId,
                DateTimeOffset.UtcNow,
                repoPath,
                request.Prompt.Length > 120 ? request.Prompt[..120] + "..." : request.Prompt,
                "codex exec --json --full-auto");
        }
        catch (OperationCanceledException)
        {
            return new StartCodexRunResponse(
                null,
                DateTimeOffset.UtcNow,
                repoPath,
                request.Prompt.Length > 120 ? request.Prompt[..120] + "..." : request.Prompt,
                "codex exec --json --full-auto");
        }
    }

    private async Task MonitorProcessAsync(Process process, string repoPath, TaskCompletionSource<string?> threadStarted, CancellationToken cancellationToken)
    {
        string? threadId = null;

        try
        {
            var stdoutTask = Task.Run(async () =>
            {
                while (!process.StandardOutput.EndOfStream)
                {
                    var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                    if (string.IsNullOrWhiteSpace(line) || !line.TrimStart().StartsWith("{", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        var root = document.RootElement;
                        if (!root.TryGetProperty("type", out var typeValue) || typeValue.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        if (typeValue.GetString() == "thread.started" &&
                            root.TryGetProperty("thread_id", out var threadIdValue) &&
                            threadIdValue.ValueKind == JsonValueKind.String)
                        {
                            threadId = threadIdValue.GetString();
                            threadStarted.TrySetResult(threadId);
                            await _stateStore.AddNotificationAsync(
                                new BridgeNotificationDto(
                                    $"agent-start:{threadId}",
                                    BridgeNotificationKind.AgentThreadStarted,
                                    "Codex thread started",
                                    $"Started a new Codex run in {repoPath}.",
                                    DateTimeOffset.UtcNow,
                                    threadId,
                                    repoPath,
                                    null),
                                cancellationToken);
                        }
                    }
                    catch (JsonException)
                    {
                        // Non-JSON warning line.
                    }
                }
            }, cancellationToken);

            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await Task.WhenAll(stdoutTask, process.WaitForExitAsync(cancellationToken));

            if (process.ExitCode != 0)
            {
                var error = await stderrTask;
                await _stateStore.AddNotificationAsync(
                    new BridgeNotificationDto(
                        $"agent-failed:{Guid.NewGuid():N}",
                        BridgeNotificationKind.AgentRunFailed,
                        "Codex run failed to start",
                        string.IsNullOrWhiteSpace(error) ? "The Codex process exited with a non-zero code." : error.Trim(),
                        DateTimeOffset.UtcNow,
                        threadId,
                        repoPath,
                        null),
                    cancellationToken);
            }

            threadStarted.TrySetResult(threadId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed while monitoring launched Codex process.");
            threadStarted.TrySetException(ex);
        }
        finally
        {
            process.Dispose();
        }
    }

    private static ProcessStartInfo CreateStartInfo(string repoPath, IReadOnlyList<string> arguments)
    {
        var executablePath = ResolveExecutableOnPath("codex");
        if (executablePath is null)
        {
            throw new FileNotFoundException("Could not find 'codex' on PATH.");
        }

        var startInfo = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = repoPath
        };

        if (OperatingSystem.IsWindows() &&
            (executablePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
             executablePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            startInfo.Arguments = $"/d /s /c \"{BuildCommandLine(executablePath, arguments)}\"";
            return startInfo;
        }

        startInfo.FileName = executablePath;
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static string? ResolveExecutableOnPath(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var extensions = OperatingSystem.IsWindows()
            ? new[] { ".cmd", ".bat", ".exe", string.Empty }
            : new[] { string.Empty };

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, command + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string BuildCommandLine(string executablePath, IReadOnlyList<string> arguments)
    {
        var parts = new List<string>(arguments.Count + 1)
        {
            QuoteForCmd(executablePath)
        };

        parts.AddRange(arguments.Select(QuoteForCmd));
        return string.Join(' ', parts);
    }

    private static string QuoteForCmd(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        if (!value.Any(ch => char.IsWhiteSpace(ch) || ch is '"' or '^' or '&' or '|' or '<' or '>'))
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
