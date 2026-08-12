using Alven.Bridge.Capabilities.Ai;
using Alven.Bridge.Capabilities.Storage;
using Alven.Bridge.Administration;
using Alven.Bridge.Configuration;
using Alven.Bridge.ControlPlane;
using Alven.Bridge.Jobs;
using Alven.Bridge.Runtime;
using Alven.Bridge.Security;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();
builder.Services.Configure<BridgeOptions>(
    builder.Configuration.GetSection(BridgeOptions.SectionName));
builder.Services.AddSingleton<IInstallationCredentialStore, InstallationCredentialStore>();
builder.Services.AddSingleton<IBridgeJobReceiptStore, BridgeJobReceiptStore>();
builder.Services.AddSingleton<BridgeRuntimeConfiguration>();
builder.Services.AddSingleton<SetupSession>();
builder.Services.AddSingleton<BridgeRuntimeState>();
builder.Services.AddSingleton<BridgeDiagnosticsService>();
builder.Services.AddHttpClient<IBridgeControlPlaneClient, BridgeControlPlaneClient>();
builder.Services.AddHttpClient<ILocalAiClient, OpenAiCompatibleLocalAiClient>();
builder.Services.AddHttpClient<RemoteStorageClient>();
builder.Services.AddSingleton<ILocalStorageClient, LocalStorageClient>();
builder.Services.AddSingleton<IBridgeJobProcessor, BridgeJobProcessor>();
builder.Services.AddHostedService<BridgeWorker>();

var app = builder.Build();
var configured = app.Configuration.GetSection(BridgeOptions.SectionName)
    .Get<BridgeOptions>() ?? new BridgeOptions();
var configurationErrors = BridgeOptionsRules.Validate(configured);
if (configurationErrors.Count > 0)
    throw new InvalidOperationException(string.Join(Environment.NewLine, configurationErrors));

app.Use(async (context, next) =>
{
    if (IsAdministrationPath(context.Request.Path)
        && !IsLocalAdministrationRequest(context.Request))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next(context);
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health/live", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/health/ready", async (BridgeRuntimeState state,
    CancellationToken cancellationToken) =>
{
    var status = await state.SnapshotAsync(cancellationToken);
    var capabilityReady = status.Capabilities.Count > 0
        && status.AiHealth is "healthy" or "disabled"
        && status.StorageHealth is "healthy" or "disabled";
    return status.Paired && status.ControlPlaneConfigured && capabilityReady
        && status.LastControlPlaneContactAt is not null
        ? Results.Ok(new { status = "ready" })
        : Results.Json(new { status = "not-ready" }, statusCode:
            StatusCodes.Status503ServiceUnavailable);
});
app.MapGet("/api/v1/status", async (BridgeRuntimeState state,
    CancellationToken cancellationToken) => Results.Ok(await state.SnapshotAsync(cancellationToken)));
app.MapGet("/api/v1/setup/session", (SetupSession session,
    BridgeRuntimeConfiguration configuration) => Results.Ok(new
    {
        nonce = session.Nonce,
        configuration = configuration.PublicSnapshot(),
    }));
app.MapPost("/api/v1/setup/verify", async (HttpContext httpContext,
    SetupSession setupSession,
    BridgeRuntimeState state,
    CancellationToken cancellationToken) =>
{
    if (!setupSession.IsValid(httpContext.Request.Headers["X-Alven-Setup-Nonce"]))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var status = await state.SnapshotAsync(cancellationToken);
    var checks = new List<BridgeSetupCheck>
    {
        status.ControlPlaneConfigured
            ? new("control-plane", "healthy", "Alven service address is configured.")
            : new("control-plane", "required", "Alven service address is missing.",
                "Use the address supplied by Alven."),
        status.Paired
            ? new("pairing", "healthy", "This Bridge is paired with a family.")
            : new("pairing", "required", "This Bridge is not paired yet.",
                "Create a one-time code in Alven and enter it in the next step."),
    };
    checks.Add(status.AiHealth switch
    {
        "healthy" => new("ai", "healthy", "Private AI answered its health check."),
        "disabled" => new("ai", "disabled", "Private AI is not enabled."),
        _ => new("ai", "unavailable", "Private AI could not be reached.",
            "Start the model server and confirm its endpoint and allowed model."),
    });
    checks.Add(status.StorageHealth switch
    {
        "healthy" => new("storage", "healthy", "Family storage answered its health check."),
        "disabled" => new("storage", "disabled", "Family storage is not enabled."),
        _ => new("storage", "unavailable", "Family storage could not be reached.",
            "Check the mount, endpoint, credentials, and write permission."),
    });
    checks.Add(status.LastControlPlaneContactAt is not null
        ? new("worker", "healthy", "The worker has contacted Alven.")
        : new("worker", status.Paired ? "checking" : "waiting",
            status.Paired ? "Waiting for the first worker heartbeat."
                : "The worker starts after pairing.",
            status.Paired ? "Keep this page open for a few seconds and check again." : null));
    var ready = status.Paired && status.ControlPlaneConfigured
        && status.LastControlPlaneContactAt is not null
        && status.AiHealth is "healthy" or "disabled"
        && status.StorageHealth is "healthy" or "disabled"
        && status.Capabilities.Count > 0;
    return Results.Ok(new BridgeSetupAssessment(ready, checks));
});
app.MapGet("/api/v1/diagnostics", async (BridgeDiagnosticsService diagnostics,
    CancellationToken cancellationToken) =>
    Results.Ok(await diagnostics.CreateAsync(cancellationToken)));
app.MapPut("/api/v1/setup/configuration", async (ConfigureBridgeRequest request,
    HttpContext httpContext, SetupSession session,
    BridgeRuntimeConfiguration configuration,
    IInstallationCredentialStore credentialStore,
    CancellationToken cancellationToken) =>
{
    if (!session.IsValid(httpContext.Request.Headers["X-Alven-Setup-Nonce"]))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    try
    {
        var paired = await credentialStore.ReadAsync(cancellationToken);
        var current = configuration.Snapshot().ControlPlaneBaseUrl.TrimEnd('/');
        var requested = request.ControlPlaneBaseUrl.Trim().TrimEnd('/');
        if (paired is not null && !string.Equals(current, requested,
                StringComparison.OrdinalIgnoreCase))
            return Results.Conflict(new { code = "unpair-before-changing-control-plane" });
        await configuration.UpdateAsync(request, cancellationToken);
        return Results.Ok(configuration.PublicSnapshot());
    }
    catch (BridgeConfigurationException exception)
    {
        return Results.BadRequest(new { code = "configuration-invalid", errors = exception.Errors });
    }
});
app.MapPost("/api/v1/pair", async (PairBridgeRequest request,
    HttpContext httpContext,
    SetupSession setupSession,
    IInstallationCredentialStore credentialStore,
    IBridgeControlPlaneClient controlPlane,
    BridgeRuntimeState state,
    CancellationToken cancellationToken) =>
{
    if (!setupSession.IsValid(httpContext.Request.Headers["X-Alven-Setup-Nonce"]))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (string.IsNullOrWhiteSpace(request.PairingCode))
        return Results.BadRequest(new { code = "pairing-code-required" });
    if (await credentialStore.ReadAsync(cancellationToken) is not null)
        return Results.Conflict(new { code = "already-paired" });
    var candidate = await credentialStore.CreateCandidateAsync(cancellationToken);
    PairInstallationResponse paired;
    try
    {
        paired = await controlPlane.PairAsync(new PairInstallationRequest(
            request.PairingCode.Trim(), candidate.InstallationId, candidate.Secret,
            state.Version, state.Capabilities), cancellationToken);
    }
    catch (Exception exception) when (exception is HttpRequestException
        or InvalidOperationException or InvalidDataException)
    {
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Alven pairing is unavailable",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "pairing-unavailable",
            });
    }
    await credentialStore.SaveAsync(candidate with
    {
        WorkspaceId = paired.WorkspaceId,
        PairedAt = paired.PairedAt,
    }, cancellationToken);
    return Results.Ok(new PairBridgeResponse(candidate.InstallationId,
        paired.WorkspaceId, paired.WorkspaceDisplayName));
});
app.MapDelete("/api/v1/pair", async (HttpContext httpContext,
    SetupSession setupSession, IInstallationCredentialStore credentialStore,
    IBridgeControlPlaneClient controlPlane,
    CancellationToken cancellationToken) =>
{
    if (!setupSession.IsValid(httpContext.Request.Headers["X-Alven-Setup-Nonce"]))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var credential = await credentialStore.ReadAsync(cancellationToken);
    if (credential is not null)
    {
        try
        {
            await controlPlane.RevokeInstallationAsync(credential, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or InvalidOperationException or InvalidDataException)
        {
            if (!httpContext.Request.Query.TryGetValue("forceLocal", out var force)
                || !string.Equals(force.ToString(), "true", StringComparison.OrdinalIgnoreCase))
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Alven could not confirm revocation",
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = "remote-revocation-unconfirmed",
                    });
        }
    }
    await credentialStore.ClearAsync(cancellationToken);
    return Results.NoContent();
});

app.MapFallbackToFile("index.html");

await app.RunAsync();

static bool IsAdministrationPath(PathString path) => path == "/" || path == "/index.html"
    || path.StartsWithSegments("/api/v1/setup")
    || path.StartsWithSegments("/api/v1/status")
    || path.StartsWithSegments("/api/v1/diagnostics")
    || path.StartsWithSegments("/api/v1/pair");

static bool IsLocalAdministrationRequest(HttpRequest request)
{
    var host = request.Host.Host;
    if (!host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        && !host.Equals("127.0.0.1", StringComparison.Ordinal)
        && !host.Equals("::1", StringComparison.Ordinal)) return false;
    if (!request.Headers.TryGetValue("Origin", out var origin)
        || string.IsNullOrWhiteSpace(origin)) return true;
    return Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var uri)
        && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("127.0.0.1", StringComparison.Ordinal)
            || uri.Host.Equals("::1", StringComparison.Ordinal));
}

public partial class Program;
