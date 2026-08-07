using System.Net.Http.Json;
using System.Text.Json;
using Alven.Bridge.Configuration;

namespace Alven.Bridge.Capabilities.Ai;

internal sealed class OpenAiCompatibleLocalAiClient(
    HttpClient httpClient,
    BridgeRuntimeConfiguration configuration) : ILocalAiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<LocalAiJobResult> CompleteAsync(LocalAiJobRequest request,
        CancellationToken cancellationToken)
    {
        var settings = configuration.Snapshot();
        if (!settings.AiEnabled) throw new LocalAiException("ai-disabled");
        if (settings.AiAllowedModels.Count == 0
            || !settings.AiAllowedModels.Contains(request.Model, StringComparer.Ordinal))
            throw new LocalAiException("model-not-allowed");
        if (request.MaximumOutputTokens is < 64 or > 4096)
            throw new LocalAiException("output-limit-invalid");
        if (request.TimeoutSeconds is < 5 or > 120)
            throw new LocalAiException("timeout-invalid");
        if (string.IsNullOrWhiteSpace(request.SystemInstruction)
            || string.IsNullOrWhiteSpace(request.Input))
            throw new LocalAiException("input-invalid");
        if (request.SystemInstruction.Length > 32_000 || request.Input.Length > 200_000
            || request.ResponseSchema.GetRawText().Length > 64_000)
            throw new LocalAiException("input-limit-exceeded");

        var endpoint = Endpoint(settings.AiBaseUrl, "chat/completions");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
        var body = new
        {
            model = request.Model,
            messages = new object[]
            {
                new { role = "system", content = request.SystemInstruction },
                new { role = "user", content = request.Input },
            },
            temperature = 0,
            max_tokens = request.MaximumOutputTokens,
            response_format = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "alven_bridge_result",
                    strict = true,
                    schema = request.ResponseSchema,
                },
            },
        };
        using var response = await httpClient.PostAsJsonAsync(endpoint, body,
            JsonOptions, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
        var content = document.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(content)) throw new LocalAiException("empty-result");
        try
        {
            using var result = JsonDocument.Parse(content);
            return new LocalAiJobResult(result.RootElement.GetRawText(), settings.AiProvider, request.Model);
        }
        catch (JsonException exception)
        {
            throw new LocalAiException("invalid-json-result", exception);
        }
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        var settings = configuration.Snapshot();
        if (!settings.AiEnabled) return false;
        try
        {
            using var response = await httpClient.GetAsync(
                Endpoint(settings.AiBaseUrl, "models"), cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException
            or TaskCanceledException or InvalidOperationException)
        {
            return false;
        }
    }

    private static Uri Endpoint(string baseUrl, string path) =>
        new(new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute), path);
}

public sealed class LocalAiException(string safeCode, Exception? inner = null)
    : Exception(safeCode, inner)
{
    public string SafeCode { get; } = safeCode;
}
