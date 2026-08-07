using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Alven.Bridge.Configuration;
using Alven.Bridge.ControlPlane;
using Alven.Bridge.Jobs;
using Microsoft.Extensions.Options;

namespace Alven.Bridge.Security;

public interface IBridgeJobReceiptStore
{
    Task<BridgeJobProcessingResult?> ReadAsync(BridgeJobEnvelope job,
        CancellationToken cancellationToken);
    Task SaveAsync(BridgeJobEnvelope job, BridgeJobProcessingResult result,
        CancellationToken cancellationToken);
    Task DeleteAsync(Guid jobId, CancellationToken cancellationToken);
    Task<int> PruneExpiredAsync(DateTimeOffset olderThan, CancellationToken cancellationToken);
}

internal sealed class BridgeJobReceiptStore : IBridgeJobReceiptStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string root;

    public BridgeJobReceiptStore(IOptions<BridgeOptions> options)
    {
        root = Path.Combine(Path.GetFullPath(options.Value.StateDirectory), "job-receipts");
    }

    public async Task<BridgeJobProcessingResult?> ReadAsync(BridgeJobEnvelope job,
        CancellationToken cancellationToken)
    {
        var path = PathFor(job.JobId);
        if (!File.Exists(path)) return null;
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var receipt = await JsonSerializer.DeserializeAsync<PersistedJobReceipt>(input,
            JsonOptions, cancellationToken) ?? throw new InvalidDataException(
                "A Bridge job receipt is invalid.");
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(receipt.Fingerprint),
                Encoding.ASCII.GetBytes(Fingerprint(job))))
            throw new BridgeJobReceiptException("job-replay-mismatch");
        return receipt.Result;
    }

    public async Task SaveAsync(BridgeJobEnvelope job, BridgeJobProcessingResult result,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(root,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = PathFor(job.JobId);
        var temporary = $"{path}.{Guid.NewGuid():N}.incoming";
        try
        {
            await using var output = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await JsonSerializer.SerializeAsync(output,
                new PersistedJobReceipt(Fingerprint(job), result, DateTimeOffset.UtcNow),
                JsonOptions, cancellationToken);
            await output.FlushAsync(cancellationToken);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public Task DeleteAsync(Guid jobId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = PathFor(jobId);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<int> PruneExpiredAsync(DateTimeOffset olderThan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(root)) return Task.FromResult(0);
        var removed = 0;
        foreach (var path in Directory.GetFiles(root, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.GetLastWriteTimeUtc(path) >= olderThan.UtcDateTime) continue;
            File.Delete(path);
            removed++;
        }
        return Task.FromResult(removed);
    }

    private string PathFor(Guid jobId) => Path.Combine(root, $"{jobId:D}.json");

    private static string Fingerprint(BridgeJobEnvelope job)
    {
        var payload = Encoding.UTF8.GetBytes($"{job.Capability}\n{job.Payload.GetRawText()}");
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    private sealed record PersistedJobReceipt(string Fingerprint,
        BridgeJobProcessingResult Result, DateTimeOffset CreatedAt);
}

public sealed class BridgeJobReceiptException(string safeCode) : Exception(safeCode)
{
    public string SafeCode { get; } = safeCode;
}
