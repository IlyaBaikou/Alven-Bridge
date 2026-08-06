using System.Reflection;
using Alven.Bridge.Configuration;
using Alven.Bridge.ControlPlane;
using Alven.Bridge.Security;
using Microsoft.Extensions.Options;

namespace Alven.Bridge.Runtime;

public sealed class BridgeRuntimeState(
    IInstallationCredentialStore credentialStore,
    IOptions<BridgeOptions> options)
{
    private string? lastSafeFailure;

    public IReadOnlyList<string> Capabilities => options.Value.Ai.Enabled
        ? ["ai.openai-compatible"]
        : [];

    public string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "0.0.0";

    public void ReportFailure(string safeCode) =>
        Volatile.Write(ref lastSafeFailure, safeCode);

    public void ReportHealthy() => Volatile.Write(ref lastSafeFailure, null);

    public async Task<BridgeRuntimeStatus> SnapshotAsync(CancellationToken cancellationToken)
    {
        var credential = await credentialStore.ReadAsync(cancellationToken);
        return new BridgeRuntimeStatus(
            credential is not null,
            credential?.InstallationId,
            !string.IsNullOrWhiteSpace(options.Value.ControlPlaneBaseUrl),
            Capabilities,
            Version,
            Volatile.Read(ref lastSafeFailure));
    }
}
