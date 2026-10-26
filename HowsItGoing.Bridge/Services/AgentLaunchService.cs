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
    private readonly IHostEnvironment _environment;
    private readonly ILogger<AgentLaunchService> _logger;

    public AgentLaunchService(
        BridgeStateStore stateStore,
        CodexSessionService sessionService,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<AgentLaunchService> logger)
    {
        _stateStore = stateStore;
        _sessionService = sessionService;
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    public async Task<StartCodexRunResponse> StartAsync(StartCodexRunRequest request, CancellationToken cancellationToken)
    {
        var repoPath = Path.GetFullPath(request.RepoPath);
        if (!Directory.Exists(repoPath))
        {
            throw new DirectoryNotFoundException($"Repo path does not exist: {repoPath}");
        }

        WorkspaceGuard.EnsureAllowed(repoPath, _configuration, _environment);

        var provider = RequestGuards.ValidateProvider(request.Provider);
        if (provider == AgentProviders.OpenCode)
        {
            return await StartOpenCodeAsync(request, repoPath, cancellationToken);
        }

        var launchCommand = CodexLaunchCommandBuilder.Build(
            request,
            repoPath,
            isGitRepository: Directory.Exists(Path.Combine(repoPath, ".git")));
        var launchSpec = CodexCliLocator.Resolve(_configuration);
        var environmentOverrides = provider == AgentProviders.CodexOpenRouter
            ? ResolveOpenRouterEnvironment()
            : null;
        var startInfo = AgentProcessFactory.CreateStartInfo(
            repoPath,
            launchSpec,
            launchCommand.Arguments,
            environmentOverrides);
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

    private async Task<StartCodexRunResponse> StartOpenCodeAsync(
        StartCodexRunRequest request,
        string repoPath,
        CancellationToken cancellationToken)
    {
        var launchCommand = OpenCodeLaunchCommandBuilder.Build(request);
        var launchSpec = AgentProcessFactory.ResolveNpmTool(
            _configuration,
            "Bridge:OpenCodeExecutablePath",
            "opencode");
        var startInfo = AgentProcessFactory.CreateStartInfo(repoPath, launchSpec, launchCommand.Arguments);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();

        // The prompt is an argv element here, so stdin is closed rather than fed. Leaving it open
        // would let the CLI block waiting for input that never arrives.
        process.StandardInput.Close();

        var logPath = Path.Combine(
            _environment.ContentRootPath,
            "logs",
            $"opencode-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log");

        await _stateStore.AddNotificationAsync(
            new BridgeNotificationDto(
                $"agent-start:opencode:{Guid.NewGuid():N}",
                BridgeNotificationKind.AgentThreadStarted,
                "OpenCode run started",
                $"Started a new OpenCode run in {repoPath}.",
                DateTimeOffset.UtcNow,
                null,
                repoPath,
                null),
            cancellationToken);

        _ = MonitorOpenCodeAsync(process, repoPath, logPath, launchCommand.LaunchMode);

        // OpenCode has no thread-id handshake to wait for; the session shows up in its own database
        // and the aggregator picks it up on the next refresh.
        return new StartCodexRunResponse(
            null,
            DateTimeOffset.UtcNow,
            repoPath,
            CreatePromptPreview(request.Prompt),
            launchCommand.LaunchMode);
    }

    private async Task MonitorOpenCodeAsync(Process process, string repoPath, string logPath, string launchMode)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            await using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
            await log.WriteLineAsync($"# {DateTimeOffset.Now:O} {launchMode}");
            await log.WriteLineAsync($"# cwd {repoPath}");

            // Both pipes must be drained or a chatty run blocks once the buffer fills. They are
            // also the only record of what a headless run did, so they go to a file the operator
            // can read; the bridge's own console is hidden when it runs from the logon launcher.
            var stdoutTask = PumpAsync(process.StandardOutput, log, prefix: null);
            var stderrTask = PumpAsync(process.StandardError, log, prefix: "stderr: ");

            await process.WaitForExitAsync();
            await Task.WhenAll(stdoutTask, stderrTask);
            await log.WriteLineAsync($"# exit code {process.ExitCode}");

            if (process.ExitCode != 0)
            {
                await _stateStore.AddNotificationAsync(
                    new BridgeNotificationDto(
                        $"agent-failed:{Guid.NewGuid():N}",
                        BridgeNotificationKind.AgentRunFailed,
                        "OpenCode run failed",
                        $"OpenCode exited with code {process.ExitCode} in {repoPath}. Log: {logPath}",
                        DateTimeOffset.UtcNow,
                        null,
                        repoPath,
                        null),
                    CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed while monitoring launched OpenCode process.");
        }
        finally
        {
            process.Dispose();
        }
    }

    private static async Task PumpAsync(StreamReader reader, StreamWriter log, string? prefix)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            await log.WriteLineAsync(prefix + line);
        }
    }

    /// <summary>
    /// The OpenRouter provider resolves its key from the child's environment (`env_key` in the Codex
    /// config), so the key has to be there before the process starts. A configured bridge value wins
    /// over the inherited user variable.
    /// </summary>
    private IReadOnlyDictionary<string, string> ResolveOpenRouterEnvironment()
    {
        var configured = _configuration["Bridge:OpenRouterApiKey"];
        var apiKey = !string.IsNullOrWhiteSpace(configured)
            ? Environment.ExpandEnvironmentVariables(configured.Trim())
            : Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "OpenRouter launches need an OPENROUTER_API_KEY user environment variable, or Bridge:OpenRouterApiKey in the bridge settings.");
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["OPENROUTER_API_KEY"] = apiKey
        };
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
