using Microsoft.Extensions.Options;

namespace HowsItGoing.Bridge.Services;

public sealed class GitHubMonitorBackgroundService : BackgroundService
{
    private readonly GitHubRepositoryService _repositoryService;
    private readonly GitHubMonitorOptions _options;
    private readonly ILogger<GitHubMonitorBackgroundService> _logger;

    public GitHubMonitorBackgroundService(
        GitHubRepositoryService repositoryService,
        IOptions<GitHubMonitorOptions> options,
        ILogger<GitHubMonitorBackgroundService> logger)
    {
        _repositoryService = repositoryService;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _repositoryService.PollForNotificationsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GitHub monitor iteration failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(15, _options.PollSeconds)), stoppingToken);
        }
    }
}
