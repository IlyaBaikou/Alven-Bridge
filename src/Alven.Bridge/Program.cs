using Alven.Bridge.Capabilities.Ai;
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
builder.Services.AddSingleton<BridgeRuntimeState>();
builder.Services.AddHttpClient<IBridgeControlPlaneClient, BridgeControlPlaneClient>();
builder.Services.AddHttpClient<ILocalAiClient, OpenAiCompatibleLocalAiClient>();
builder.Services.AddSingleton<IBridgeJobProcessor, BridgeJobProcessor>();
builder.Services.AddHostedService<BridgeWorker>();

var app = builder.Build();
var configured = app.Configuration.GetSection(BridgeOptions.SectionName)
    .Get<BridgeOptions>() ?? new BridgeOptions();
var configurationErrors = BridgeOptionsRules.Validate(configured);
if (configurationErrors.Count > 0)
    throw new InvalidOperationException(string.Join(Environment.NewLine, configurationErrors));

app.MapGet("/health/live", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/api/v1/status", async (BridgeRuntimeState state,
    CancellationToken cancellationToken) => Results.Ok(await state.SnapshotAsync(cancellationToken)));
app.MapPost("/api/v1/pair", async (PairBridgeRequest request,
    IInstallationCredentialStore credentialStore,
    IBridgeControlPlaneClient controlPlane,
    BridgeRuntimeState state,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.PairingCode))
        return Results.BadRequest(new { code = "pairing-code-required" });
    if (await credentialStore.ReadAsync(cancellationToken) is not null)
        return Results.Conflict(new { code = "already-paired" });
    var candidate = await credentialStore.CreateCandidateAsync(cancellationToken);
    var paired = await controlPlane.PairAsync(new PairInstallationRequest(
        request.PairingCode.Trim(), candidate.InstallationId, candidate.Secret,
        state.Version, state.Capabilities), cancellationToken);
    await credentialStore.SaveAsync(candidate with { PairedAt = paired.PairedAt }, cancellationToken);
    return Results.Ok(new PairBridgeResponse(candidate.InstallationId,
        paired.WorkspaceId, paired.WorkspaceDisplayName));
});
app.MapDelete("/api/v1/pair", async (IInstallationCredentialStore credentialStore,
    CancellationToken cancellationToken) =>
{
    await credentialStore.ClearAsync(cancellationToken);
    return Results.NoContent();
});

await app.RunAsync();

public partial class Program;
