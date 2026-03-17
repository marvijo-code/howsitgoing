using HowsItGoing.Bridge.Services;
using HowsItGoing.Bridge.State;
using HowsItGoing.Contracts;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls(builder.Configuration["Bridge:Urls"] ?? "http://0.0.0.0:5217");

builder.Services.AddOpenApi();
builder.Services.Configure<BridgeOptions>(builder.Configuration.GetSection(BridgeOptions.SectionName));
builder.Services.Configure<GitHubMonitorOptions>(builder.Configuration.GetSection(GitHubMonitorOptions.SectionName));
builder.Services.AddHttpClient();
builder.Services.AddSingleton<BridgeStateStore>();
builder.Services.AddSingleton<CodexThreadParser>();
builder.Services.AddSingleton<CodexSessionService>();
builder.Services.AddSingleton<GitHubRepositoryService>();
builder.Services.AddSingleton<AgentLaunchService>();
builder.Services.AddHostedService<CodexCompletionMonitorService>();
builder.Services.AddHostedService<GitHubMonitorBackgroundService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/settings", (IConfiguration configuration, IHostEnvironment environment) =>
{
    var codexHome = BridgeOptions.ResolveCodexHome(configuration);
    var gitHubOptions = configuration.GetSection(GitHubMonitorOptions.SectionName).Get<GitHubMonitorOptions>() ?? new GitHubMonitorOptions();
    var monitoredRepoPath = GitHubMonitorOptions.ResolveMonitoredRepositoryPath(environment.ContentRootPath, gitHubOptions.MonitoredRepositoryPath);
    var suggestedBaseUrl = configuration["Bridge:SuggestedBaseUrl"] ?? "http://10.0.2.2:5217";
    var gitHubRepo = GitHubMonitorOptions.TryExtractRepositorySlug(GitHubMonitorOptions.ResolveRemoteUrl(monitoredRepoPath));
    var bridgeOptions = configuration.GetSection(BridgeOptions.SectionName).Get<BridgeOptions>() ?? new BridgeOptions();

    return Results.Ok(new BridgeSettingsDto(
        suggestedBaseUrl,
        codexHome,
        monitoredRepoPath,
        gitHubRepo is null ? null : $"{gitHubRepo.Value.Owner}/{gitHubRepo.Value.Name}",
        bridgeOptions.NotificationPollSeconds,
        gitHubOptions.PollSeconds));
});

app.MapGet("/api/sessions", async (
    string? query,
    string? status,
    string? source,
    bool? includeArchived,
    CodexSessionService sessions,
    CancellationToken cancellationToken) =>
{
    CodexSessionStatus? parsedStatus = null;
    if (!string.IsNullOrWhiteSpace(status) &&
        Enum.TryParse<CodexSessionStatus>(status, true, out var statusValue))
    {
        parsedStatus = statusValue;
    }

    var results = await sessions.GetSessionsAsync(query, parsedStatus, source, includeArchived ?? false, cancellationToken);
    return Results.Ok(results);
});

app.MapGet("/api/notifications", async (
    DateTimeOffset? since,
    int? limit,
    BridgeStateStore stateStore,
    CancellationToken cancellationToken) =>
{
    var results = await stateStore.GetNotificationsAsync(since, limit ?? 50, cancellationToken);
    return Results.Ok(results);
});

app.MapGet("/api/repository/status", async (
    GitHubRepositoryService gitHubRepositoryService,
    CancellationToken cancellationToken) =>
{
    var status = await gitHubRepositoryService.GetStatusAsync(cancellationToken);
    return Results.Ok(status);
});

app.MapGet("/api/update", async (
    int? currentVersionCode,
    string? currentVersion,
    GitHubRepositoryService gitHubRepositoryService,
    CancellationToken cancellationToken) =>
{
    var result = await gitHubRepositoryService.GetLatestUpdateAsync(currentVersionCode, currentVersion, cancellationToken);
    return Results.Ok(result);
});

app.MapPost("/api/agent/start-run", async (
    StartCodexRunRequest request,
    AgentLaunchService launcher,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.RepoPath) || string.IsNullOrWhiteSpace(request.Prompt))
    {
        return Results.BadRequest("Both repoPath and prompt are required.");
    }

    var response = await launcher.StartAsync(request, cancellationToken);
    return Results.Ok(response);
});

app.Run();
