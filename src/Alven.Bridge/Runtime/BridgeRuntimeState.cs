using System.Reflection;
using Alven.Bridge.Configuration;
using Alven.Bridge.ControlPlane;
using Alven.Bridge.Security;
using Alven.Bridge.Capabilities.Ai;
using Alven.Bridge.Capabilities.Storage;

namespace Alven.Bridge.Runtime;

public sealed class BridgeRuntimeState(
    IInstallationCredentialStore credentialStore,
    BridgeRuntimeConfiguration configuration,
    ILocalAiClient aiClient,
    ILocalStorageClient storageClient)
{
    private string? lastSafeFailure;

    public IReadOnlyList<string> Capabilities
    {
        get
        {
            var settings = configuration.Snapshot();
            var capabilities = new List<string>();
            if (settings.AiEnabled) capabilities.Add("ai.openai-compatible");
            if (settings.StorageEnabled)
            {
                capabilities.Add("storage.stat");
                capabilities.Add("storage.read");
                if (!settings.StorageReadOnly)
                {
                    capabilities.Add("storage.write");
                    capabilities.Add("storage.delete");
                }
            }
            return capabilities;
        }
    }

    public string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "0.0.0";

    public void ReportFailure(string safeCode) =>
        Volatile.Write(ref lastSafeFailure, safeCode);

    public void ReportHealthy() => Volatile.Write(ref lastSafeFailure, null);

    public async Task<BridgeRuntimeStatus> SnapshotAsync(CancellationToken cancellationToken)
    {
        var credential = await credentialStore.ReadAsync(cancellationToken);
        var settings = configuration.Snapshot();
        var aiHealth = !settings.AiEnabled ? "disabled"
            : await aiClient.IsHealthyAsync(cancellationToken) ? "healthy" : "unavailable";
        var storageHealth = !settings.StorageEnabled ? "disabled"
            : await storageClient.IsHealthyAsync(cancellationToken) ? "healthy" : "unavailable";
        return new BridgeRuntimeStatus(
            credential is not null,
            credential?.InstallationId,
            !string.IsNullOrWhiteSpace(configuration.Snapshot().ControlPlaneBaseUrl),
            Capabilities,
            Version,
            Volatile.Read(ref lastSafeFailure),
            aiHealth,
            storageHealth);
    }
}
