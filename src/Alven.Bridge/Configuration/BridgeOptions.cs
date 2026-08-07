namespace Alven.Bridge.Configuration;

public sealed class BridgeOptions
{
    public const string SectionName = "Bridge";

    public string StateDirectory { get; init; } = ".bridge-state";
    public string ControlPlaneBaseUrl { get; init; } = string.Empty;
    public int PollIntervalSeconds { get; init; } = 10;
    public int HeartbeatIntervalSeconds { get; init; } = 30;
    public LocalAiOptions Ai { get; init; } = new();
    public LocalStorageOptions Storage { get; init; } = new();
}

public sealed class LocalAiOptions
{
    public bool Enabled { get; init; } = true;
    public string Provider { get; init; } = "ollama";
    public string BaseUrl { get; init; } = "http://127.0.0.1:11434/v1/";
    public IReadOnlyList<string> AllowedModels { get; init; } = [];
    public int MaximumTimeoutSeconds { get; init; } = 120;
    public int MaximumOutputTokens { get; init; } = 4096;
}

public sealed class LocalStorageOptions
{
    public bool Enabled { get; init; }
    public string RootPath { get; init; } = "/data/family-files";
    public bool ReadOnly { get; init; }
    public long MaximumFileBytes { get; init; } = 100_000_000;
}

public static class BridgeOptionsRules
{
    public static IReadOnlyList<string> Validate(BridgeOptions options)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(options.StateDirectory))
            errors.Add("Bridge:StateDirectory is required.");
        if (options.PollIntervalSeconds is < 2 or > 300)
            errors.Add("Bridge:PollIntervalSeconds must be between 2 and 300.");
        if (options.HeartbeatIntervalSeconds is < 5 or > 600)
            errors.Add("Bridge:HeartbeatIntervalSeconds must be between 5 and 600.");

        if (!string.IsNullOrWhiteSpace(options.ControlPlaneBaseUrl))
        {
            if (!Uri.TryCreate(options.ControlPlaneBaseUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && !IsLoopback(uri)))
            {
                errors.Add("Bridge:ControlPlaneBaseUrl must use HTTPS unless it is loopback development.");
            }
        }

        if (options.Ai.Enabled)
        {
            if (!Uri.TryCreate(options.Ai.BaseUrl, UriKind.Absolute, out var aiUri)
                || (aiUri.Scheme != Uri.UriSchemeHttp && aiUri.Scheme != Uri.UriSchemeHttps))
                errors.Add("Bridge:Ai:BaseUrl must be an absolute HTTP or HTTPS URL.");
            if (options.Ai.MaximumTimeoutSeconds is < 5 or > 600)
                errors.Add("Bridge:Ai:MaximumTimeoutSeconds must be between 5 and 600.");
            if (options.Ai.MaximumOutputTokens is < 64 or > 32768)
                errors.Add("Bridge:Ai:MaximumOutputTokens must be between 64 and 32768.");
        }

        if (options.Storage.Enabled)
        {
            if (string.IsNullOrWhiteSpace(options.Storage.RootPath)
                || !Path.IsPathFullyQualified(options.Storage.RootPath))
                errors.Add("Bridge:Storage:RootPath must be an absolute mounted path.");
            if (options.Storage.MaximumFileBytes is < 1_000_000 or > 5_000_000_000)
                errors.Add("Bridge:Storage:MaximumFileBytes must be between 1 MB and 5 GB.");
        }

        return errors;
    }

    private static bool IsLoopback(Uri uri) =>
        uri.IsLoopback || uri.Host.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase);
}
