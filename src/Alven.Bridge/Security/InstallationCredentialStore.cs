using System.Security.Cryptography;
using System.Text.Json;
using Alven.Bridge.Configuration;
using Microsoft.Extensions.Options;

namespace Alven.Bridge.Security;

public sealed record InstallationCredential(
    Guid InstallationId,
    string Secret,
    DateTimeOffset PairedAt,
    int Generation);

public interface IInstallationCredentialStore
{
    Task<InstallationCredential?> ReadAsync(CancellationToken cancellationToken);
    Task<InstallationCredential> CreateCandidateAsync(CancellationToken cancellationToken);
    Task SaveAsync(InstallationCredential credential, CancellationToken cancellationToken);
    Task ClearAsync(CancellationToken cancellationToken);
}

internal sealed class InstallationCredentialStore : IInstallationCredentialStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string credentialPath;

    public InstallationCredentialStore(IOptions<BridgeOptions> options)
    {
        var root = Path.GetFullPath(options.Value.StateDirectory);
        credentialPath = Path.Combine(root, "installation.json");
    }

    public async Task<InstallationCredential?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(credentialPath)) return null;
        await using var input = new FileStream(credentialPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var value = await JsonSerializer.DeserializeAsync<InstallationCredential>(input, JsonOptions,
            cancellationToken);
        return value is not null && value.InstallationId != Guid.Empty
            && !string.IsNullOrWhiteSpace(value.Secret)
            ? value
            : throw new InvalidDataException("The installation credential file is invalid.");
    }

    public Task<InstallationCredential> CreateCandidateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Task.FromResult(new InstallationCredential(Guid.NewGuid(), secret,
            DateTimeOffset.UtcNow, 1));
    }

    public async Task SaveAsync(InstallationCredential credential,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(credentialPath)!;
        Directory.CreateDirectory(directory);
        TryProtectDirectory(directory);
        var temporary = $"{credentialPath}.{Guid.NewGuid():N}.incoming";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(output, credential, JsonOptions, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            TryProtectFile(temporary);
            File.Move(temporary, credentialPath, true);
            TryProtectFile(credentialPath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(credentialPath)) File.Delete(credentialPath);
        return Task.CompletedTask;
    }

    private static void TryProtectDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void TryProtectFile(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
