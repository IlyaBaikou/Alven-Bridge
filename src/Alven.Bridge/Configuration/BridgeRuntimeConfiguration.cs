using System.Text.Json;
using Alven.Bridge.Administration;
using Microsoft.Extensions.Options;

namespace Alven.Bridge.Configuration;

public sealed record BridgeEditableSettings(
    string ControlPlaneBaseUrl,
    int PollIntervalSeconds,
    int HeartbeatIntervalSeconds,
    bool AiEnabled,
    string AiProvider,
    string AiBaseUrl,
    IReadOnlyList<string> AiAllowedModels,
    bool StorageEnabled,
    string StorageProvider,
    string StorageRootPath,
    string StorageEndpoint,
    string StorageBucket,
    string StoragePrefix,
    string StorageUsername,
    string StoragePassword,
    string StorageAccessKey,
    string StorageSecretKey,
    string StorageRegion,
    bool StorageReadOnly,
    long StorageMaximumFileBytes,
    int ReceiptRetentionDays);

public sealed class BridgeRuntimeConfiguration : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string settingsPath;
    private BridgeEditableSettings current;

    public BridgeRuntimeConfiguration(IOptions<BridgeOptions> options)
    {
        var defaults = options.Value;
        settingsPath = Path.Combine(Path.GetFullPath(defaults.StateDirectory), "settings.json");
        current = Load(settingsPath) ?? FromOptions(defaults);
        var errors = Validate(current);
        if (errors.Count > 0)
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
    }

    public BridgeEditableSettings Snapshot() => Volatile.Read(ref current);
    public BridgeEditableSettings PublicSnapshot() => Snapshot() with
    {
        StoragePassword = string.Empty,
        StorageAccessKey = string.Empty,
        StorageSecretKey = string.Empty,
    };

    public async Task<BridgeEditableSettings> UpdateAsync(ConfigureBridgeRequest request,
        CancellationToken cancellationToken)
    {
        var candidate = new BridgeEditableSettings(
            request.ControlPlaneBaseUrl.Trim(),
            Math.Clamp(request.PollIntervalSeconds, 2, 300),
            Math.Clamp(request.HeartbeatIntervalSeconds, 5, 600),
            request.AiEnabled,
            request.AiProvider.Trim().ToLowerInvariant(),
            request.AiBaseUrl.Trim(),
            request.AiAllowedModels.Select(item => item.Trim())
                .Where(item => item.Length > 0).Distinct(StringComparer.Ordinal).ToArray(),
            request.StorageEnabled,
            (request.StorageProvider ?? "mounted").Trim().ToLowerInvariant(),
            request.StorageRootPath.Trim(),
            request.StorageEndpoint?.Trim() ?? string.Empty,
            request.StorageBucket?.Trim() ?? string.Empty,
            request.StoragePrefix?.Trim().Trim('/') ?? string.Empty,
            string.IsNullOrWhiteSpace(request.StorageUsername)
                ? current.StorageUsername : request.StorageUsername.Trim(),
            string.IsNullOrEmpty(request.StoragePassword) ? current.StoragePassword : request.StoragePassword,
            string.IsNullOrWhiteSpace(request.StorageAccessKey)
                ? current.StorageAccessKey : request.StorageAccessKey.Trim(),
            string.IsNullOrEmpty(request.StorageSecretKey) ? current.StorageSecretKey : request.StorageSecretKey,
            string.IsNullOrWhiteSpace(request.StorageRegion) ? "us-east-1" : request.StorageRegion.Trim(),
            request.StorageReadOnly,
            request.StorageMaximumFileBytes,
            request.ReceiptRetentionDays);
        var errors = Validate(candidate);
        if (errors.Count > 0) throw new BridgeConfigurationException(errors);

        await gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(settingsPath)!;
            Directory.CreateDirectory(directory);
            var temporary = $"{settingsPath}.{Guid.NewGuid():N}.incoming";
            try
            {
                await using var output = new FileStream(temporary, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                await JsonSerializer.SerializeAsync(output, candidate, JsonOptions,
                    cancellationToken);
                await output.FlushAsync(cancellationToken);
                Protect(temporary);
                File.Move(temporary, settingsPath, true);
                Protect(settingsPath);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            Volatile.Write(ref current, candidate);
            return candidate;
        }
        finally
        {
            gate.Release();
        }
    }

    private static BridgeEditableSettings FromOptions(BridgeOptions options) => new(
        options.ControlPlaneBaseUrl, options.PollIntervalSeconds,
        options.HeartbeatIntervalSeconds, options.Ai.Enabled, options.Ai.Provider,
        options.Ai.BaseUrl, options.Ai.AllowedModels, options.Storage.Enabled,
        options.Storage.Provider, options.Storage.RootPath, options.Storage.Endpoint,
        options.Storage.Bucket, options.Storage.Prefix, options.Storage.Username,
        options.Storage.Password, options.Storage.AccessKey, options.Storage.SecretKey,
        options.Storage.Region, options.Storage.ReadOnly,
        options.Storage.MaximumFileBytes, options.ReceiptRetentionDays);

    private static BridgeEditableSettings? Load(string path)
    {
        if (!File.Exists(path)) return null;
        using var input = File.OpenRead(path);
        var loaded = JsonSerializer.Deserialize<BridgeEditableSettings>(input, JsonOptions);
        return loaded is null ? null : loaded with
        {
            ReceiptRetentionDays = loaded.ReceiptRetentionDays == 0 ? 30 : loaded.ReceiptRetentionDays,
            StorageProvider = string.IsNullOrWhiteSpace(loaded.StorageProvider)
                ? "mounted" : loaded.StorageProvider,
            StorageEndpoint = loaded.StorageEndpoint ?? string.Empty,
            StorageBucket = loaded.StorageBucket ?? string.Empty,
            StoragePrefix = loaded.StoragePrefix ?? string.Empty,
            StorageUsername = loaded.StorageUsername ?? string.Empty,
            StoragePassword = loaded.StoragePassword ?? string.Empty,
            StorageAccessKey = loaded.StorageAccessKey ?? string.Empty,
            StorageSecretKey = loaded.StorageSecretKey ?? string.Empty,
            StorageRegion = string.IsNullOrWhiteSpace(loaded.StorageRegion)
                ? "us-east-1" : loaded.StorageRegion,
        };
    }

    private static IReadOnlyList<string> Validate(BridgeEditableSettings settings)
    {
        var options = new BridgeOptions
        {
            StateDirectory = "/runtime-validation",
            ControlPlaneBaseUrl = settings.ControlPlaneBaseUrl,
            PollIntervalSeconds = settings.PollIntervalSeconds,
            HeartbeatIntervalSeconds = settings.HeartbeatIntervalSeconds,
            ReceiptRetentionDays = settings.ReceiptRetentionDays,
            Ai = new LocalAiOptions
            {
                Enabled = settings.AiEnabled,
                Provider = settings.AiProvider,
                BaseUrl = settings.AiBaseUrl,
                AllowedModels = settings.AiAllowedModels,
            },
            Storage = new LocalStorageOptions
            {
                Enabled = settings.StorageEnabled,
                Provider = settings.StorageProvider,
                RootPath = settings.StorageRootPath,
                Endpoint = settings.StorageEndpoint,
                Bucket = settings.StorageBucket,
                Prefix = settings.StoragePrefix,
                Username = settings.StorageUsername,
                Password = settings.StoragePassword,
                AccessKey = settings.StorageAccessKey,
                SecretKey = settings.StorageSecretKey,
                Region = settings.StorageRegion,
                ReadOnly = settings.StorageReadOnly,
                MaximumFileBytes = settings.StorageMaximumFileBytes,
            },
        };
        return BridgeOptionsRules.Validate(options);
    }

    private static void Protect(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public void Dispose()
    {
        gate.Dispose();
        GC.SuppressFinalize(this);
    }
}

public sealed class BridgeConfigurationException(IReadOnlyList<string> errors)
    : Exception("Bridge configuration is invalid.")
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
