using System.Diagnostics;
using HowsItGoing.Bridge.State;
using HowsItGoing.Contracts;
using Microsoft.Extensions.Configuration;

namespace HowsItGoing.Bridge.Services;

/// <summary>
/// Sends a follow-up message to an existing Codex, Claude Code, or OpenCode session by
/// resuming it headlessly through the matching CLI.
/// </summary>
public sealed class FollowUpService
{
    private readonly BridgeStateStore _stateStore;
    private readonly AgentSessionAggregator _sessionAggregator;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<FollowUpService> _logger;

    public FollowUpService(
        BridgeStateStore stateStore,
        AgentSessionAggregator sessionAggregator,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<FollowUpService> logger)
    {
        _stateStore = stateStore;
        _sessionAggregator = sessionAggregator;
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    public async Task<SessionFollowUpResponse> SendAsync(SessionFollowUpRequest request, CancellationToken cancellationToken)
    {
        var agent = AgentKinds.Normalize(request.Agent)
            ?? throw new ArgumentException("An agent kind is required (codex, claude, or opencode).");
        if (agent is not (AgentKinds.Codex or AgentKinds.ClaudeCode or AgentKinds.OpenCode))
        {
            throw new ArgumentException($"Unknown agent kind: {request.Agent}");
        }

        var sessionId = RequestGuards.ValidateSessionId(request.SessionId);
        var message = request.Message.Trim();

        var workingDirectory = request.WorkingDirectory?.Trim();
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            var session = await _sessionAggregator.FindSessionAsync(agent, sessionId, cancellationToken);
            workingDirectory = session?.WorkingDirectory;
        }

        // No user-profile fallback: resuming an auto-approving agent against the whole home
        // directory is never what the caller meant, and it used to be reachable by sending an
        // unknown session id.
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            throw new ArgumentException(
                $"Could not determine a working directory for session {sessionId}. Pass an existing workingDirectory.");
        }

        WorkspaceGuard.EnsureAllowed(workingDirectory, _configuration, _environment);

        var (launchSpec, arguments, stdinInput) = BuildLaunch(agent, sessionId, message);
        var environmentOverrides = agent == AgentKinds.ClaudeCode ? ReadClaudeEnvironment() : null;
        var startInfo = AgentProcessFactory.CreateStartInfo(workingDirectory, launchSpec, arguments, environmentOverrides);
        var launchMode = $"{agent}: {string.Join(' ', arguments)}";

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();
        if (stdinInput is not null)
        {
            await process.StandardInput.WriteAsync(stdinInput.AsMemory(), cancellationToken);
            await process.StandardInput.WriteAsync(Environment.NewLine.AsMemory(), cancellationToken);
        }

        process.StandardInput.Close();

        _ = MonitorProcessAsync(process, agent, sessionId, workingDirectory);

        return new SessionFollowUpResponse(agent, sessionId, DateTimeOffset.UtcNow, launchMode);
    }

    private (CodexProcessLaunchSpec LaunchSpec, IReadOnlyList<string> Arguments, string? StdinInput) BuildLaunch(
        string agent,
        string sessionId,
        string message)
    {
        switch (agent)
        {
            case AgentKinds.Codex:
            {
                var spec = CodexCliLocator.Resolve(_configuration);
                return (spec, ["exec", "resume", sessionId, "--json", "--full-auto", "-"], message);
            }

            case AgentKinds.ClaudeCode:
            {
                var spec = AgentProcessFactory.ResolveNpmTool(_configuration, "Bridge:ClaudeExecutablePath", "claude");
                return (spec, ["-p", "--resume", sessionId, "--dangerously-skip-permissions"], message);
            }

            default:
            {
                var spec = AgentProcessFactory.ResolveNpmTool(_configuration, "Bridge:OpenCodeExecutablePath", "opencode");
                return (spec, ["run", "-s", sessionId, "--auto", message], null);
            }
        }
    }

    /// <summary>
    /// Optional env overrides for launched `claude` processes (Bridge:ClaudeEnvironment section).
    /// Lets machines without a CLI OAuth login route follow-ups through a token-authenticated
    /// Anthropic-compatible endpoint instead.
    /// </summary>
    private Dictionary<string, string>? ReadClaudeEnvironment()
    {
        var section = _configuration.GetSection("Bridge:ClaudeEnvironment");
        var overrides = section.GetChildren()
            .Where(child => !string.IsNullOrWhiteSpace(child.Value))
            .ToDictionary(child => child.Key, child => child.Value!, StringComparer.OrdinalIgnoreCase);
        return overrides.Count == 0 ? null : overrides;
    }

    private async Task MonitorProcessAsync(Process process, string agent, string sessionId, string workingDirectory)
    {
        var agentName = AgentKinds.DisplayName(agent);
        try
        {
            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await _stateStore.AddNotificationAsync(
                    new BridgeNotificationDto(
                        $"follow-up-failed:{sessionId}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
                        BridgeNotificationKind.FollowUpFailed,
                        $"{agentName} follow-up timed out",
                        $"The follow-up turn on session {sessionId} was killed after 30 minutes without finishing.",
                        DateTimeOffset.UtcNow,
                        sessionId,
                        workingDirectory,
                        null),
                    CancellationToken.None);
                return;
            }

            var error = (await stderrTask).Trim();
            var output = (await stdoutTask).Trim();

            if (process.ExitCode == 0)
            {
                // Codex runs with --json, so its stdout is an event stream rather than reply text.
                var replyPreview = agent == AgentKinds.Codex ? null : Tail(output, 300);
                await _stateStore.AddNotificationAsync(
                    new BridgeNotificationDto(
                        $"follow-up-done:{sessionId}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
                        BridgeNotificationKind.FollowUpCompleted,
                        $"{agentName} follow-up finished",
                        string.IsNullOrWhiteSpace(replyPreview)
                            ? $"The follow-up turn on session {sessionId} in {workingDirectory} completed."
                            : replyPreview,
                        DateTimeOffset.UtcNow,
                        sessionId,
                        workingDirectory,
                        null),
                    CancellationToken.None);
            }
            else
            {
                var detail = !string.IsNullOrWhiteSpace(error)
                    ? error
                    : !string.IsNullOrWhiteSpace(output)
                        ? $"Exit code {process.ExitCode}: {Tail(output, 400)}"
                        : $"The {agentName} process exited with code {process.ExitCode}.";
                await _stateStore.AddNotificationAsync(
                    new BridgeNotificationDto(
                        $"follow-up-failed:{sessionId}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
                        BridgeNotificationKind.FollowUpFailed,
                        $"{agentName} follow-up failed",
                        detail,
                        DateTimeOffset.UtcNow,
                        sessionId,
                        workingDirectory,
                        null),
                    CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed while monitoring a {Agent} follow-up process for session {SessionId}.", agent, sessionId);
        }
        finally
        {
            process.Dispose();
        }
    }

    private static string Tail(string value, int maxLength) =>
        value.Length <= maxLength ? value : "…" + value[^maxLength..];
}
