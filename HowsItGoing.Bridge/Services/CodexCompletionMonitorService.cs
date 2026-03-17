using HowsItGoing.Bridge.State;
using HowsItGoing.Contracts;

namespace HowsItGoing.Bridge.Services;

public sealed class CodexCompletionMonitorService : BackgroundService
{
    private readonly CodexSessionService _sessionService;
    private readonly BridgeStateStore _stateStore;
    private readonly ILogger<CodexCompletionMonitorService> _logger;

    public CodexCompletionMonitorService(
        CodexSessionService sessionService,
        BridgeStateStore stateStore,
        ILogger<CodexCompletionMonitorService> logger)
    {
        _sessionService = sessionService;
        _stateStore = stateStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var sessions = await _sessionService.GetRecentUnarchivedSessionsAsync(100, stoppingToken);
                foreach (var session in sessions.Where(x => x.Status == CodexSessionStatus.Completed))
                {
                    if (!await _stateStore.TryRecordCompletedThreadAsync(session.Id, stoppingToken))
                    {
                        continue;
                    }

                    await _stateStore.AddNotificationAsync(
                        new BridgeNotificationDto(
                            $"codex-complete:{session.Id}",
                            BridgeNotificationKind.CodexThreadCompleted,
                            session.Title,
                            session.LastAgentMessage ?? "Codex marked the thread as complete.",
                            session.CompletedAt ?? session.UpdatedAt,
                            session.Id,
                            session.WorkingDirectory,
                            null),
                        stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Codex completion monitor iteration failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}
