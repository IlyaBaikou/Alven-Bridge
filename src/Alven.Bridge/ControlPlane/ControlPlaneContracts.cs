using System.Text.Json;

namespace Alven.Bridge.ControlPlane;

public sealed record PairInstallationRequest(
    string PairingCode,
    Guid InstallationId,
    string InstallationSecret,
    string Version,
    IReadOnlyList<string> Capabilities);

public sealed record PairInstallationResponse(
    Guid WorkspaceId,
    string WorkspaceDisplayName,
    DateTimeOffset PairedAt);

public sealed record IssueInstallationTokenRequest(Guid InstallationId, string InstallationSecret);

public sealed record IssueInstallationTokenResponse(string AccessToken, DateTimeOffset ExpiresAt);

public sealed record BridgeHeartbeatRequest(
    string Version,
    IReadOnlyList<string> Capabilities,
    DateTimeOffset ObservedAt);

public sealed record BridgeJobEnvelope(
    Guid JobId,
    string LeaseToken,
    string Capability,
    JsonElement Payload,
    DateTimeOffset ExpiresAt);

public sealed record BridgeJobCompletionRequest(
    string LeaseToken,
    string Outcome,
    JsonElement? Result,
    string? SafeFailureCode,
    DateTimeOffset CompletedAt);

public sealed record BridgeRuntimeStatus(
    bool Paired,
    Guid? InstallationId,
    bool ControlPlaneConfigured,
    IReadOnlyList<string> Capabilities,
    string Version,
    string? LastSafeFailure);
