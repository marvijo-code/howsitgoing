using HowsItGoing.Bridge.State;
using HowsItGoing.Services;

namespace HowsItGoing.Bridge.Services;

public sealed class SharedBridgeSyncService : BackgroundService
{
    private readonly SharedBridgeStore _sharedStore;
    private readonly AgentSessionAggregator _sessionService;
    private readonly BridgeStateStore _stateStore;
    private readonly AgentLaunchService _agentLaunchService;
    private readonly ILogger<SharedBridgeSyncService> _logger;
    private readonly string _processorId = $"{Environment.MachineName}:{Environment.ProcessId}";
    private DateTimeOffset _lastSyncAt = DateTimeOffset.MinValue;

    public SharedBridgeSyncService(
        SharedBridgeStore sharedStore,
        AgentSessionAggregator sessionService,
        BridgeStateStore stateStore,
        AgentLaunchService agentLaunchService,
        ILogger<SharedBridgeSyncService> logger)
    {
        _sharedStore = sharedStore;
        _sessionService = sessionService;
        _stateStore = stateStore;
        _agentLaunchService = agentLaunchService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_sharedStore.IsConfigured)
        {
            _logger.LogInformation("Shared store is not configured; remote sync and queued launch processing are disabled.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncMirrorIfDueAsync(stoppingToken);

                var processedCommand = await ProcessNextQueuedCommandAsync(stoppingToken);
                var delay = processedCommand
                    ? TimeSpan.FromSeconds(1)
                    : TimeSpan.FromSeconds(_sharedStore.CommandPollSeconds);

                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Shared bridge sync iteration failed.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task SyncMirrorIfDueAsync(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - _lastSyncAt < TimeSpan.FromSeconds(_sharedStore.SyncIntervalSeconds))
        {
            return;
        }

        var sessions = await _sessionService.GetSessionsAsync(
            query: null,
            status: null,
            source: null,
            agent: null,
            includeArchived: true,
            cancellationToken);
        var notifications = await _stateStore.GetNotificationsAsync(since: null, limit: 200, cancellationToken);

        await _sharedStore.UpsertSessionsAsync(sessions, cancellationToken);
        await _sharedStore.UpsertNotificationsAsync(notifications, cancellationToken);
        _lastSyncAt = DateTimeOffset.UtcNow;
    }

    private async Task<bool> ProcessNextQueuedCommandAsync(CancellationToken cancellationToken)
    {
        var command = await _sharedStore.ClaimPendingCommandAsync(_processorId, cancellationToken);
        if (command is null)
        {
            return false;
        }

        try
        {
            var response = await _agentLaunchService.StartAsync(command.Request, cancellationToken);
            await _sharedStore.MarkCommandStartedAsync(command.Id, response, cancellationToken);
            await SyncMirrorIfDueAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            var message = string.IsNullOrWhiteSpace(ex.Message)
                ? "The bridge failed to start the queued Codex run."
                : ex.Message;
            await _sharedStore.MarkCommandFailedAsync(command.Id, message, cancellationToken);
        }

        return true;
    }
}
