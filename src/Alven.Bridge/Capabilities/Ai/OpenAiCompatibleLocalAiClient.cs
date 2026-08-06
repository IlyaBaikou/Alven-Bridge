using System.Net.Http.Json;
using System.Text.Json;
using Alven.Bridge.Configuration;
using Microsoft.Extensions.Options;

namespace Alven.Bridge.Capabilities.Ai;

internal sealed class OpenAiCompatibleLocalAiClient(
    HttpClient httpClient,
    IOptions<BridgeOptions> options) : ILocalAiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<LocalAiJobResult> CompleteAsync(LocalAiJobRequest request,
        CancellationToken cancellationToken)
    {
        var ai = options.Value.Ai;
        if (!ai.Enabled) throw new LocalAiException("ai-disabled");
        if (ai.AllowedModels.Count == 0
            || !ai.AllowedModels.Contains(request.Model, StringComparer.Ordinal))
            throw new LocalAiException("model-not-allowed");
        if (request.MaximumOutputTokens is < 64 || request.MaximumOutputTokens > ai.MaximumOutputTokens)
            throw new LocalAiException("output-limit-invalid");
        if (request.TimeoutSeconds is < 5 || request.TimeoutSeconds > ai.MaximumTimeoutSeconds)
            throw new LocalAiException("timeout-invalid");
        if (string.IsNullOrWhiteSpace(request.SystemInstruction)
            || string.IsNullOrWhiteSpace(request.Input))
            throw new LocalAiException("input-invalid");

        ConfigureBaseAddress(ai.BaseUrl);
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
        using var response = await httpClient.PostAsJsonAsync("chat/completions", body,
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
            return new LocalAiJobResult(result.RootElement.GetRawText(), ai.Provider, request.Model);
        }
        catch (JsonException exception)
        {
            throw new LocalAiException("invalid-json-result", exception);
        }
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Ai.Enabled) return false;
        try
        {
            ConfigureBaseAddress(options.Value.Ai.BaseUrl);
            using var response = await httpClient.GetAsync("models", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException
            or TaskCanceledException or InvalidOperationException)
        {
            return false;
        }
    }

    private void ConfigureBaseAddress(string baseUrl) =>
        httpClient.BaseAddress ??= new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
}

public sealed class LocalAiException(string safeCode, Exception? inner = null)
    : Exception(safeCode, inner)
{
    public string SafeCode { get; } = safeCode;
}
