namespace Alven.Bridge.Administration;

public sealed record PairBridgeRequest(string PairingCode);

public sealed record PairBridgeResponse(
    Guid InstallationId,
    Guid WorkspaceId,
    string WorkspaceDisplayName);

public sealed record ConfigureBridgeRequest(
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
    long StorageMaximumFileBytes,
    int ReceiptRetentionDays,
    string StorageProvider = "mounted",
    string? StorageEndpoint = null,
    string? StorageBucket = null,
    string? StoragePrefix = null,
    string? StorageUsername = null,
    string? StoragePassword = null,
    string? StorageAccessKey = null,
    string? StorageSecretKey = null,
    string? StorageRegion = null);

public sealed class SetupSession
{
    private readonly string nonce = Convert.ToHexString(
        System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public string Nonce => nonce;
    public bool IsValid(string? candidate) => !string.IsNullOrWhiteSpace(candidate)
        && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(candidate),
            System.Text.Encoding.UTF8.GetBytes(nonce));
}
