using System.Text.Json;

namespace Alven.Bridge.Capabilities.Ai;

public sealed record LocalAiJobRequest(
    string Capability,
    string Model,
    string SystemInstruction,
    string Input,
    JsonElement ResponseSchema,
    int MaximumOutputTokens,
    int TimeoutSeconds);

public sealed record LocalAiJobResult(string Json, string Provider, string Model);

public interface ILocalAiClient
{
    Task<LocalAiJobResult> CompleteAsync(LocalAiJobRequest request,
        CancellationToken cancellationToken);
    Task<bool> IsHealthyAsync(CancellationToken cancellationToken);
}
