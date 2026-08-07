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
    string StorageRootPath,
    bool StorageReadOnly,
    long StorageMaximumFileBytes);

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
            request.StorageRootPath.Trim(),
            request.StorageReadOnly,
            request.StorageMaximumFileBytes);
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
        options.Storage.RootPath, options.Storage.ReadOnly,
        options.Storage.MaximumFileBytes);

    private static BridgeEditableSettings? Load(string path)
    {
        if (!File.Exists(path)) return null;
        using var input = File.OpenRead(path);
        return JsonSerializer.Deserialize<BridgeEditableSettings>(input, JsonOptions);
    }

    private static IReadOnlyList<string> Validate(BridgeEditableSettings settings)
    {
        var options = new BridgeOptions
        {
            StateDirectory = "/runtime-validation",
            ControlPlaneBaseUrl = settings.ControlPlaneBaseUrl,
            PollIntervalSeconds = settings.PollIntervalSeconds,
            HeartbeatIntervalSeconds = settings.HeartbeatIntervalSeconds,
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
                RootPath = settings.StorageRootPath,
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
