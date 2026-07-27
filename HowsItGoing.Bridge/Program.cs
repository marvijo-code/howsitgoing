using HowsItGoing.Bridge.Security;
using HowsItGoing.Bridge.Services;
using HowsItGoing.Bridge.State;
using HowsItGoing.Contracts;
using HowsItGoing.Services;

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
builder.Services.AddSingleton<PairedDeviceStore>();
builder.Services.AddSingleton<PairingService>();
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

// ---- Device pairing -------------------------------------------------------------------------
// Opening a pairing window is itself a privileged action, so /api/pair/start goes through the
// normal auth gate (loopback by default). Only the redeem endpoint is anonymous.

app.MapPost("/api/pair/start", (PairingService pairing) => Results.Ok(pairing.StartPairing()));

app.MapPost("/api/pair/cancel", (PairingService pairing) =>
{
    pairing.CancelPairing();
    return Results.NoContent();
});

app.MapGet("/api/pair/status", (PairingService pairing) => Results.Ok(new { isPairingActive = pairing.IsPairingActive }));

app.MapPost("/api/pair", async (
    PairingRedeemRequest request,
    PairingService pairing,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Code))
    {
        return Results.BadRequest("A pairing code is required.");
    }

    var response = await pairing.TryRedeemAsync(request.Code, request.DeviceName, cancellationToken);

    // Deliberately one opaque failure for wrong code, expired code, and no open window: a caller
    // that cannot pair should not learn which of those it hit.
    return response is null
        ? Results.Json(
            new { error = "pairing_failed", detail = "That code is not valid. Start pairing again on the bridge host." },
            statusCode: StatusCodes.Status401Unauthorized)
        : Results.Ok(response);
});

app.MapGet("/api/pair/devices", async (PairedDeviceStore devices, CancellationToken cancellationToken) =>
    Results.Ok(await devices.ListAsync(cancellationToken)));

app.MapDelete("/api/pair/devices/{deviceId}", async (
    string deviceId,
    PairedDeviceStore devices,
    CancellationToken cancellationToken) =>
    await devices.RevokeAsync(deviceId, cancellationToken) ? Results.NoContent() : Results.NotFound());

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
