using Alven.Bridge.Configuration;
using Alven.Bridge.ControlPlane;
using Alven.Bridge.Runtime;
using Microsoft.Extensions.Options;

namespace Alven.Bridge.Administration;

public sealed record BridgeDiagnostics(
    DateTimeOffset GeneratedAt,
    string Version,
    bool Paired,
    bool ControlPlaneConfigured,
    IReadOnlyList<string> Capabilities,
    string AiHealth,
    string StorageHealth,
    string? LastSafeFailure,
    int PendingReceiptCount,
    DateTimeOffset? OldestReceiptAt,
    long StateBytes,
    string RedactionNotice);

internal sealed class BridgeDiagnosticsService(
    BridgeRuntimeState runtimeState,
    IOptions<BridgeOptions> options)
{
    private readonly string stateDirectory = Path.GetFullPath(options.Value.StateDirectory);

    public async Task<BridgeDiagnostics> CreateAsync(CancellationToken cancellationToken)
    {
        var status = await runtimeState.SnapshotAsync(cancellationToken);
        var receipts = Path.Combine(stateDirectory, "job-receipts");
        var receiptFiles = Directory.Exists(receipts)
            ? Directory.GetFiles(receipts, "*.json", SearchOption.TopDirectoryOnly) : [];
        var stateFiles = Directory.Exists(stateDirectory)
            ? Directory.GetFiles(stateDirectory, "*", SearchOption.AllDirectories) : [];
        return new(DateTimeOffset.UtcNow, status.Version, status.Paired,
            status.ControlPlaneConfigured, status.Capabilities, status.AiHealth,
            status.StorageHealth, status.LastSafeFailure, receiptFiles.Length,
            receiptFiles.Length == 0 ? null : receiptFiles
                .Select(path => new FileInfo(path).CreationTimeUtc)
                .Min(),
            stateFiles.Sum(path => new FileInfo(path).Length),
            "No prompts, results, file names, file bytes, paths, URLs, models, tokens, or secrets are included.");
    }
}
