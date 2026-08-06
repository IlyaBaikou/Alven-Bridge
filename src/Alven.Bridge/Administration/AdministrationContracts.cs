namespace Alven.Bridge.Administration;

public sealed record PairBridgeRequest(string PairingCode);

public sealed record PairBridgeResponse(
    Guid InstallationId,
    Guid WorkspaceId,
    string WorkspaceDisplayName);
