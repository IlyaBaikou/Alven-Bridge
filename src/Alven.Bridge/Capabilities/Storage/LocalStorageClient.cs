using System.Security.Cryptography;
using System.Text.Json;
using Alven.Bridge.Configuration;
using Microsoft.Extensions.Options;

namespace Alven.Bridge.Capabilities.Storage;

public sealed record LocalStorageJobRequest(
    string RelativePath,
    string? ContentBase64,
    string? ExpectedSha256,
    bool Overwrite = false);

public sealed record LocalStorageJobResult(
    string RelativePath,
    long SizeBytes,
    string Sha256,
    string? ContentBase64 = null,
    bool Missing = false);

public interface ILocalStorageClient
{
    Task<LocalStorageJobResult> ProcessAsync(string capability, JsonElement payload,
        CancellationToken cancellationToken);
    Task<bool> IsHealthyAsync(CancellationToken cancellationToken);
}

internal sealed class LocalStorageClient(BridgeRuntimeConfiguration configuration,
    IOptions<BridgeOptions> options)
    : ILocalStorageClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string MountMarkerName = ".alven-bridge-mount-id";
    private readonly string stateDirectory = Path.GetFullPath(options.Value.StateDirectory);

    public async Task<LocalStorageJobResult> ProcessAsync(string capability,
        JsonElement payload, CancellationToken cancellationToken)
    {
        var settings = configuration.Snapshot();
        if (!settings.StorageEnabled) throw new LocalStorageException("storage-disabled");
        EnsureMountIdentity(settings);
        LocalStorageJobRequest request;
        try
        {
            request = payload.Deserialize<LocalStorageJobRequest>(JsonOptions)
                ?? throw new LocalStorageException("storage-payload-invalid");
        }
        catch (JsonException exception)
        {
            throw new LocalStorageException("storage-payload-invalid", exception);
        }
        var root = Path.GetFullPath(settings.StorageRootPath);
        var path = Resolve(root, request.RelativePath);
        return capability switch
        {
            "storage.stat" => Stat(path, request.RelativePath,
                settings.StorageMaximumFileBytes),
            "storage.read" => await ReadAsync(path, request.RelativePath,
                settings.StorageMaximumFileBytes, cancellationToken),
            "storage.write" when !settings.StorageReadOnly => await WriteAsync(root, path,
                request, settings.StorageMaximumFileBytes, cancellationToken),
            "storage.delete" when !settings.StorageReadOnly => Delete(path, request.RelativePath),
            "storage.write" or "storage.delete" =>
                throw new LocalStorageException("storage-read-only"),
            _ => throw new LocalStorageException("storage-capability-unsupported"),
        };
    }

    private static LocalStorageJobResult Stat(string path, string relativePath,
        long maximumBytes)
    {
        if (!File.Exists(path)) return new(relativePath, 0, string.Empty, Missing: true);
        var info = new FileInfo(path);
        if (info.Length > maximumBytes) throw new LocalStorageException("storage-file-too-large");
        using var input = File.OpenRead(path);
        var checksum = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        return new(relativePath, info.Length, checksum);
    }

    public Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = configuration.Snapshot();
        if (!settings.StorageEnabled) return Task.FromResult(false);
        try
        {
            EnsureMountIdentity(settings);
            return Task.FromResult(true);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException or LocalStorageException)
        {
            return Task.FromResult(false);
        }
    }

    private static async Task<LocalStorageJobResult> ReadAsync(string path,
        string relativePath, long maximumBytes, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return new(relativePath, 0, string.Empty, Missing: true);
        var info = new FileInfo(path);
        if (info.Length > maximumBytes) throw new LocalStorageException("storage-file-too-large");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return new(relativePath, bytes.LongLength, Sha256(bytes),
            Convert.ToBase64String(bytes));
    }

    private static async Task<LocalStorageJobResult> WriteAsync(string root, string path,
        LocalStorageJobRequest request, long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ContentBase64))
            throw new LocalStorageException("storage-content-required");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(request.ContentBase64); }
        catch (FormatException exception)
        {
            throw new LocalStorageException("storage-content-invalid", exception);
        }
        if (bytes.LongLength > maximumBytes)
            throw new LocalStorageException("storage-file-too-large");
        var checksum = Sha256(bytes);
        if (!string.IsNullOrWhiteSpace(request.ExpectedSha256)
            && !string.Equals(checksum, request.ExpectedSha256.Trim().ToLowerInvariant(),
                StringComparison.Ordinal))
            throw new LocalStorageException("storage-checksum-mismatch");
        if (File.Exists(path) && !request.Overwrite)
            throw new LocalStorageException("storage-file-exists");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RejectLinks(root, Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.incoming";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, path, request.Overwrite);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return new(request.RelativePath, bytes.LongLength, checksum);
    }

    private static LocalStorageJobResult Delete(string path, string relativePath)
    {
        if (File.Exists(path)) File.Delete(path);
        return new(relativePath, 0, string.Empty, Missing: !File.Exists(path));
    }

    private static string Resolve(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new LocalStorageException("storage-path-invalid");
        if (relativePath.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment.StartsWith(".alven-bridge-", StringComparison.Ordinal)))
            throw new LocalStorageException("storage-path-reserved");
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        var boundary = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(boundary, StringComparison.Ordinal))
            throw new LocalStorageException("storage-path-invalid");
        RejectLinks(root, Path.GetDirectoryName(candidate)!);
        if (File.Exists(candidate) && new FileInfo(candidate).LinkTarget is not null)
            throw new LocalStorageException("storage-link-rejected");
        return candidate;
    }

    private static void RejectLinks(string root, string path)
    {
        var current = new DirectoryInfo(path);
        while (current.Exists)
        {
            if (current.LinkTarget is not null)
                throw new LocalStorageException("storage-link-rejected");
            if (string.Equals(current.FullName, root, StringComparison.Ordinal)) return;
            current = current.Parent ?? throw new LocalStorageException("storage-path-invalid");
        }
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
            .ToLowerInvariant();

    private void EnsureMountIdentity(BridgeEditableSettings settings)
    {
        var root = Path.GetFullPath(settings.StorageRootPath);
        if (!Directory.Exists(root))
        {
            if (settings.StorageReadOnly)
                throw new LocalStorageException("storage-mount-unavailable");
            Directory.CreateDirectory(root);
        }
        RejectLinks(root, root);
        Directory.CreateDirectory(stateDirectory);
        var rootKey = Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(root))).ToLowerInvariant()[..24];
        var stateMarker = Path.Combine(stateDirectory, $"storage-mount-{rootKey}.id");
        var rootMarker = Path.Combine(root, MountMarkerName);
        var expected = File.Exists(stateMarker) ? File.ReadAllText(stateMarker).Trim() : null;
        var observed = File.Exists(rootMarker) ? File.ReadAllText(rootMarker).Trim() : null;
        if (!string.IsNullOrWhiteSpace(expected))
        {
            if (string.IsNullOrWhiteSpace(observed)
                || !CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(expected),
                    System.Text.Encoding.ASCII.GetBytes(observed)))
                throw new LocalStorageException("storage-mount-changed");
            return;
        }
        if (string.IsNullOrWhiteSpace(observed))
        {
            if (settings.StorageReadOnly)
                throw new LocalStorageException("storage-mount-not-initialized");
            observed = Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
                .ToLowerInvariant();
            WriteProtected(rootMarker, observed);
        }
        WriteProtected(stateMarker, observed);
    }

    private static void WriteProtected(string path, string value)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.incoming";
        try
        {
            File.WriteAllText(temporary, value);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

public sealed class LocalStorageException(string safeCode, Exception? inner = null)
    : Exception(safeCode, inner)
{
    public string SafeCode { get; } = safeCode;
}
