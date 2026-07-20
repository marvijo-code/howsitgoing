using System.Diagnostics;
using System.Text.Json;
using HowsItGoing.Bridge.State;
using HowsItGoing.Contracts;
using Microsoft.Extensions.Configuration;

namespace HowsItGoing.Bridge.Services;

public sealed class AgentLaunchService
{
    private readonly BridgeStateStore _stateStore;
    private readonly CodexSessionService _sessionService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AgentLaunchService> _logger;

    public AgentLaunchService(
        BridgeStateStore stateStore,
        CodexSessionService sessionService,
        IConfiguration configuration,
        ILogger<AgentLaunchService> logger)
    {
        _stateStore = stateStore;
        _sessionService = sessionService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<StartCodexRunResponse> StartAsync(StartCodexRunRequest request, CancellationToken cancellationToken)
    {
        var repoPath = Path.GetFullPath(request.RepoPath);
        if (!Directory.Exists(repoPath))
        {
            throw new DirectoryNotFoundException($"Repo path does not exist: {repoPath}");
        }

        var launchCommand = CodexLaunchCommandBuilder.Build(
            request,
            repoPath,
            isGitRepository: Directory.Exists(Path.Combine(repoPath, ".git")));
        var launchSpec = CodexCliLocator.Resolve(_configuration);
        var startInfo = AgentProcessFactory.CreateStartInfo(repoPath, launchSpec, launchCommand.Arguments);
        var launchMode = string.Join(' ', launchCommand.Arguments);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var launchResult = new TaskCompletionSource<StartCodexRunResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        process.Start();
        await process.StandardInput.WriteAsync(launchCommand.PromptInput.AsMemory(), cancellationToken);
        await process.StandardInput.WriteAsync(Environment.NewLine.AsMemory(), cancellationToken);
        process.StandardInput.Close();

        _ = MonitorProcessAsync(process, repoPath, request.Prompt, launchCommand, launchMode, launchResult, cancellationToken);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var registration = linked.Token.Register(() =>
            launchResult.TrySetException(new TimeoutException("Codex did not confirm a real thread in the Codex state database before the start timeout.")));

        return await launchResult.Task.WaitAsync(linked.Token);
    }

    private async Task MonitorProcessAsync(
        Process process,
        string repoPath,
        string prompt,
        CodexLaunchCommand launchCommand,
        string launchMode,
        TaskCompletionSource<StartCodexRunResponse> launchResult,
        CancellationToken cancellationToken)
    {
        string? emittedThreadId = null;
        string? confirmedThreadId = null;

        try
        {
            var stdoutTask = Task.Run(async () =>
            {
                while (!process.StandardOutput.EndOfStream)
                {
                    var line = await process.StandardOutput.ReadLineAsync();
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
                            emittedThreadId = threadIdValue.GetString();
                            if (!string.IsNullOrWhiteSpace(emittedThreadId) &&
                                await WaitForThreadRegistrationAsync(emittedThreadId, cancellationToken))
                            {
                                confirmedThreadId = emittedThreadId;
                                var response = new StartCodexRunResponse(
                                    confirmedThreadId,
                                    DateTimeOffset.UtcNow,
                                    repoPath,
                                    CreatePromptPreview(prompt),
                                    launchMode);
                                launchResult.TrySetResult(response);
                                await _stateStore.AddNotificationAsync(
                                    new BridgeNotificationDto(
                                        $"agent-start:{confirmedThreadId}",
                                        BridgeNotificationKind.AgentThreadStarted,
                                        "Codex thread started",
                                        $"Started a new Codex run in {repoPath}.",
                                        DateTimeOffset.UtcNow,
                                        confirmedThreadId,
                                        repoPath,
                                        null),
                                    CancellationToken.None);
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // Non-JSON warning line.
                    }
                }
            });

            var stderrTask = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdoutTask, process.WaitForExitAsync());
            var error = (await stderrTask).Trim();

            if (confirmedThreadId is not null)
            {
                return;
            }

            if (process.ExitCode != 0)
            {
                var failureMessage = BuildFailureMessage(process.ExitCode, launchCommand, error, emittedThreadId);
                await _stateStore.AddNotificationAsync(
                    new BridgeNotificationDto(
                        $"agent-failed:{Guid.NewGuid():N}",
                        BridgeNotificationKind.AgentRunFailed,
                        "Codex run failed to start",
                        failureMessage,
                        DateTimeOffset.UtcNow,
                        emittedThreadId,
                        repoPath,
                        null),
                    CancellationToken.None);
                launchResult.TrySetException(new InvalidOperationException(failureMessage));
            }
            else
            {
                var failureMessage = BuildMissingConfirmationMessage(emittedThreadId, error);
                await _stateStore.AddNotificationAsync(
                    new BridgeNotificationDto(
                        $"agent-failed:{Guid.NewGuid():N}",
                        BridgeNotificationKind.AgentRunFailed,
                        emittedThreadId is null ? "Codex run exited without a thread id" : "Codex thread was never confirmed",
                        failureMessage,
                        DateTimeOffset.UtcNow,
                        emittedThreadId,
                        repoPath,
                        null),
                    CancellationToken.None);
                launchResult.TrySetException(new InvalidOperationException(failureMessage));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed while monitoring launched Codex process.");
            launchResult.TrySetException(ex);
        }
        finally
        {
            process.Dispose();
        }
    }

    private async Task<bool> WaitForThreadRegistrationAsync(string threadId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 15; attempt++)
        {
            if (await _sessionService.ThreadExistsAsync(threadId, cancellationToken))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        return false;
    }

    private static string BuildFailureMessage(int exitCode, CodexLaunchCommand launchCommand, string error, string? emittedThreadId)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            return error;
        }

        var suffix = string.IsNullOrWhiteSpace(emittedThreadId)
            ? "before reporting a thread id"
            : $"after emitting thread id {emittedThreadId}, which never appeared in the Codex state database";
        return $"The Codex process exited with code {exitCode} {suffix}. Launch arguments: {string.Join(' ', launchCommand.Arguments)}";
    }

    private static string BuildMissingConfirmationMessage(string? emittedThreadId, string error)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            return error;
        }

        if (string.IsNullOrWhiteSpace(emittedThreadId))
        {
            return "Codex exited successfully but did not emit a thread.started event before termination.";
        }

        return $"Codex emitted thread id {emittedThreadId}, but that thread never appeared in the Codex state database.";
    }

    private static string CreatePromptPreview(string prompt)
    {
        var trimmed = prompt.Trim();
        return trimmed.Length > 120 ? trimmed[..120] + "..." : trimmed;
    }
}
