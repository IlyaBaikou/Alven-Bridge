namespace Alven.Bridge.Configuration;

public sealed class BridgeOptions
{
    public const long MaximumRelayFileBytes = 5_000_000;
    public const string SectionName = "Bridge";

    public string StateDirectory { get; init; } = ".bridge-state";
    public string ControlPlaneBaseUrl { get; init; } = string.Empty;
    public int PollIntervalSeconds { get; init; } = 10;
    public int HeartbeatIntervalSeconds { get; init; } = 30;
    public int ReceiptRetentionDays { get; init; } = 30;
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
    public string Provider { get; init; } = "mounted";
    public string RootPath { get; init; } = "/data/family-files";
    public string Endpoint { get; init; } = string.Empty;
    public string Bucket { get; init; } = string.Empty;
    public string Prefix { get; init; } = "alven";
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string AccessKey { get; init; } = string.Empty;
    public string SecretKey { get; init; } = string.Empty;
    public string Region { get; init; } = "us-east-1";
    public bool ReadOnly { get; init; }
    public long MaximumFileBytes { get; init; } = BridgeOptions.MaximumRelayFileBytes;
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
        if (options.ReceiptRetentionDays is < 1 or > 90)
            errors.Add("Bridge:ReceiptRetentionDays must be between 1 and 90.");

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
            if (options.Storage.Provider == "mounted"
                && (string.IsNullOrWhiteSpace(options.Storage.RootPath)
                    || !Path.IsPathFullyQualified(options.Storage.RootPath)))
                errors.Add("Bridge:Storage:RootPath must be an absolute mounted path.");
            if (options.Storage.Provider is "webdav" or "s3")
            {
                if (!Uri.TryCreate(options.Storage.Endpoint, UriKind.Absolute, out var endpoint)
                    || endpoint.Scheme != Uri.UriSchemeHttp
                        && endpoint.Scheme != Uri.UriSchemeHttps)
                    errors.Add("Bridge:Storage:Endpoint must be an absolute HTTP or HTTPS URL.");
            }
            if (options.Storage.Provider == "webdav"
                && (string.IsNullOrWhiteSpace(options.Storage.Username)
                    || string.IsNullOrWhiteSpace(options.Storage.Password)))
                errors.Add("WebDAV username and password are required.");
            if (options.Storage.Provider == "s3"
                && (string.IsNullOrWhiteSpace(options.Storage.Bucket)
                    || string.IsNullOrWhiteSpace(options.Storage.AccessKey)
                    || string.IsNullOrWhiteSpace(options.Storage.SecretKey)))
                errors.Add("S3 bucket, access key, and secret key are required.");
            if (options.Storage.Provider is not ("mounted" or "webdav" or "s3"))
                errors.Add("Bridge:Storage:Provider must be mounted, webdav, or s3.");
            if (options.Storage.MaximumFileBytes is < 1_000_000
                or > BridgeOptions.MaximumRelayFileBytes)
            {
                errors.Add("Bridge:Storage:MaximumFileBytes must be between 1 MB and the 5 MB preview relay limit.");
            }
        }

        return errors;
    }

    private static bool IsLoopback(Uri uri) =>
        uri.IsLoopback || uri.Host.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase);
}
