using HowsItGoing.Bridge.Push;
using HowsItGoing.Bridge.Security;
using HowsItGoing.Bridge.Services;
using HowsItGoing.Bridge.State;
using HowsItGoing.Contracts;
using HowsItGoing.Services;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// Loopback by default. Binding wider is a deliberate opt-in that must be paired with
// Bridge:AccessToken - BridgeAccessMiddleware refuses non-loopback callers without one.
builder.WebHost.UseUrls(builder.Configuration["Bridge:Urls"] ?? "http://127.0.0.1:5217");

builder.Services.AddOpenApi();

// The WASM head runs on its own origin, so the browser preflights every bridge call. Only that
// origin is allowed: a wildcard would let any page the developer visits drive the bridge, and in
// the token-less loopback mode those requests would arrive from 127.0.0.1 and be trusted.
var allowedOrigins = builder.Configuration.GetSection("Bridge:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:5219", "http://127.0.0.1:5219"];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()));

builder.Services.Configure<BridgeOptions>(builder.Configuration.GetSection(BridgeOptions.SectionName));
builder.Services.Configure<GitHubMonitorOptions>(builder.Configuration.GetSection(GitHubMonitorOptions.SectionName));
builder.Services.Configure<WebPushOptions>(builder.Configuration.GetSection(WebPushOptions.SectionName));
builder.Services.AddHttpClient();
builder.Services.AddSingleton(_ => SharedStoreOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton<SharedBridgeStore>();
builder.Services.AddSingleton<NotificationBroadcaster>();
builder.Services.AddSingleton<WebPushSubscriptionStore>();
builder.Services.AddSingleton<WebPushSender>();
builder.Services.AddSingleton<WebPushDispatcher>();
builder.Services.AddHostedService(static services => services.GetRequiredService<WebPushDispatcher>());
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
app.UseMiddleware<BridgeAccessMiddleware>();

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
    catch (UnauthorizedAccessException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden);
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

// ---- Web push for the feed -------------------------------------------------------------------
// A browser needs the VAPID public key before it can subscribe, so this is the one push endpoint
// that answers before anything is registered.
app.MapGet("/api/push/config", async (
    WebPushSubscriptionStore pushStore,
    IOptions<WebPushOptions> pushOptions,
    CancellationToken cancellationToken) =>
{
    if (!pushOptions.Value.Enabled)
    {
        return Results.Ok(new WebPushConfigDto(false, null, null));
    }

    var keys = await pushStore.GetVapidKeysAsync(cancellationToken);
    return Results.Ok(new WebPushConfigDto(true, keys.PublicKey, pushOptions.Value.Subject));
});

app.MapGet("/api/push/subscription", async (
    string endpoint,
    WebPushSubscriptionStore pushStore,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(endpoint))
    {
        return Results.BadRequest("endpoint is required.");
    }

    var subscription = await pushStore.FindAsync(endpoint, cancellationToken);
    return Results.Ok(subscription is null
        ? new WebPushSubscriptionStatusDto(false, null, new WebPushPreferencesDto(), null, null)
        : new WebPushSubscriptionStatusDto(
            true,
            subscription.Label,
            subscription.Preferences,
            subscription.UpdatedAt,
            subscription.LastPushedAt));
});

// Doubles as the "save my filters" call: the browser re-posts the same endpoint with new preferences.
app.MapPost("/api/push/subscribe", async (
    WebPushSubscribeRequest request,
    WebPushSubscriptionStore pushStore,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Endpoint) ||
        string.IsNullOrWhiteSpace(request.Keys?.P256dh) ||
        string.IsNullOrWhiteSpace(request.Keys?.Auth))
    {
        return Results.BadRequest("endpoint, keys.p256dh and keys.auth are required.");
    }

    try
    {
        var subscription = await pushStore.UpsertAsync(request, cancellationToken);
        return Results.Ok(new WebPushSubscriptionStatusDto(
            true,
            subscription.Label,
            subscription.Preferences,
            subscription.UpdatedAt,
            subscription.LastPushedAt));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(ex.Message);
    }
});

app.MapPost("/api/push/unsubscribe", async (
    WebPushUnsubscribeRequest request,
    WebPushSubscriptionStore pushStore,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Endpoint))
    {
        return Results.BadRequest("endpoint is required.");
    }

    await pushStore.RemoveAsync(request.Endpoint, cancellationToken);
    return Results.Ok(new WebPushSubscriptionStatusDto(false, null, new WebPushPreferencesDto(), null, null));
});

// Bypasses the filters on purpose: this is how a user checks the plumbing works, and having it
// silently swallowed by their own quiet hours would be worse than useless.
app.MapPost("/api/push/test", async (
    WebPushUnsubscribeRequest request,
    WebPushDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Endpoint))
    {
        return Results.BadRequest("endpoint is required.");
    }

    var result = await dispatcher.DispatchAsync(
        new BridgeNotificationDto(
            $"push-test:{Guid.NewGuid():N}",
            BridgeNotificationKind.AgentThreadStarted,
            "How's It Going",
            "Test push - the feed can reach this browser.",
            DateTimeOffset.UtcNow,
            null,
            null,
            null),
        cancellationToken,
        onlyEndpoint: request.Endpoint,
        ignorePreferences: true);

    return Results.Ok(result);
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

    try
    {
        var response = await launcher.StartAsync(request, cancellationToken);
        return Results.Ok(response);
    }
    catch (UnauthorizedAccessException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(ex.Message);
    }
    catch (DirectoryNotFoundException ex)
    {
        return Results.BadRequest(ex.Message);
    }
});

app.Run();
