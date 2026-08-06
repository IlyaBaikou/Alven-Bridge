using System.Text.Json;
using Alven.Bridge.Capabilities.Ai;
using Alven.Bridge.ControlPlane;

namespace Alven.Bridge.Jobs;

public sealed record BridgeJobProcessingResult(
    string Outcome,
    JsonElement? Result,
    string? SafeFailureCode);

public interface IBridgeJobProcessor
{
    Task<BridgeJobProcessingResult> ProcessAsync(BridgeJobEnvelope job,
        CancellationToken cancellationToken);
}

internal sealed class BridgeJobProcessor(ILocalAiClient aiClient) : IBridgeJobProcessor
{
    public async Task<BridgeJobProcessingResult> ProcessAsync(BridgeJobEnvelope job,
        CancellationToken cancellationToken)
    {
        if (job.ExpiresAt <= DateTimeOffset.UtcNow)
            return new("rejected", null, "job-expired");
        if (!job.Capability.StartsWith("ai.", StringComparison.Ordinal))
            return new("rejected", null, "capability-unsupported");
        LocalAiJobRequest? request;
        try
        {
            request = job.Payload.Deserialize<LocalAiJobRequest>();
        }
        catch (JsonException)
        {
            return new("rejected", null, "payload-invalid");
        }
        if (request is null || !string.Equals(job.Capability, request.Capability,
            StringComparison.Ordinal)) return new("rejected", null, "capability-mismatch");
        try
        {
            var result = await aiClient.CompleteAsync(request, cancellationToken);
            using var json = JsonDocument.Parse(result.Json);
            return new("completed", json.RootElement.Clone(), null);
        }
        catch (LocalAiException exception)
        {
            return new("failed", null, exception.SafeCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new("failed", null, "local-ai-timeout");
        }
        catch (HttpRequestException)
        {
            return new("failed", null, "local-ai-unavailable");
        }
    }
}
