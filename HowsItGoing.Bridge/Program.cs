using HowsItGoing.Bridge.Services;
using HowsItGoing.Bridge.State;
using HowsItGoing.Contracts;
using HowsItGoing.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.WebHost.UseUrls(builder.Configuration["Bridge:Urls"] ?? "http://0.0.0.0:5217");

builder.Services.AddOpenApi();

// The WASM head runs on a different origin (its own dev/static host), so the browser
// preflights every bridge call. The bridge is loopback-only developer tooling.
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .AllowAnyOrigin()
    .AllowAnyHeader()
    .AllowAnyMethod()));

builder.Services.Configure<BridgeOptions>(builder.Configuration.GetSection(BridgeOptions.SectionName));
builder.Services.Configure<GitHubMonitorOptions>(builder.Configuration.GetSection(GitHubMonitorOptions.SectionName));
builder.Services.AddHttpClient();
builder.Services.AddSingleton(_ => SharedStoreOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton<SharedBridgeStore>();
builder.Services.AddSingleton<BridgeStateStore>();
builder.Services.AddSingleton<CodexThreadParser>();
builder.Services.AddSingleton<ICodexRuntimeStateProvider>(static services => services.GetRequiredService<CodexThreadParser>());
builder.Services.AddSingleton<CodexSessionService>();
builder.Services.AddSingleton<ClaudeCodeSessionService>();
builder.Services.AddSingleton<OpenCodeSessionService>();
builder.Services.AddSingleton<AgentSessionAggregator>();
builder.Services.AddSingleton<GitHubRepositoryService>();
builder.Services.AddSingleton<GitHubIssueService>();
builder.Services.AddSingleton<AgentLaunchService>();
builder.Services.AddSingleton<FollowUpService>();
builder.Services.AddHostedService<CodexCompletionMonitorService>();
builder.Services.AddHostedService<GitHubMonitorBackgroundService>();
builder.Services.AddHostedService<SharedBridgeSyncService>();

var app = builder.Build();

app.UseCors();

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
    string? agent,
    bool? includeArchived,
    AgentSessionAggregator sessions,
    CancellationToken cancellationToken) =>
{
    CodexSessionStatus? parsedStatus = null;
    if (!string.IsNullOrWhiteSpace(status) &&
        Enum.TryParse<CodexSessionStatus>(status, true, out var statusValue))
    {
        parsedStatus = statusValue;
    }

    var results = await sessions.GetSessionsAsync(query, parsedStatus, source, agent, includeArchived ?? false, cancellationToken);
    return Results.Ok(results);
});

app.MapPost("/api/sessions/follow-up", async (
    SessionFollowUpRequest request,
    FollowUpService followUps,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.SessionId) || string.IsNullOrWhiteSpace(request.Message))
    {
        return Results.BadRequest("Both sessionId and message are required.");
    }

    try
    {
        var response = await followUps.SendAsync(request, cancellationToken);
        return Results.Ok(response);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(ex.Message);
    }
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

app.MapGet("/api/issues", async (
    string? repo,
    string? state,
    GitHubIssueService issues,
    CancellationToken cancellationToken) =>
{
    var repositoryFilter = string.IsNullOrWhiteSpace(repo)
        ? null
        : repo.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    var board = await issues.GetBoardAsync(repositoryFilter, state, cancellationToken);
    return Results.Ok(board);
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
