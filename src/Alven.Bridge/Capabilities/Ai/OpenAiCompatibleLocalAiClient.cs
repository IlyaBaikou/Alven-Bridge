using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
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

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
        var content = IsOllama(settings.AiProvider)
            ? await CompleteWithOllamaAsync(settings, request, timeout.Token)
            : await CompleteWithOpenAiCompatibleAsync(settings, request, timeout.Token);
        if (string.IsNullOrWhiteSpace(content)) throw new LocalAiException("empty-result");
        try
        {
            using var result = JsonDocument.Parse(content);
            return new LocalAiJobResult(result.RootElement.GetRawText(), settings.AiProvider,
                request.Model);
        }
        catch (JsonException exception)
        {
            throw new LocalAiException("invalid-json-result", exception);
        }
    }

    private async Task<string?> CompleteWithOpenAiCompatibleAsync(
        BridgeEditableSettings settings, LocalAiJobRequest request,
        CancellationToken cancellationToken)
    {
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
        using var response = await httpClient.PostAsJsonAsync(
            Endpoint(settings.AiBaseUrl, "chat/completions"), body, JsonOptions,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        return document.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString();
    }

    private async Task<string?> CompleteWithOllamaAsync(BridgeEditableSettings settings,
        LocalAiJobRequest request, CancellationToken cancellationToken)
    {
        // Ollama's OpenAI compatibility layer converts a JSON schema to a grammar. Large bounded
        // strings and regexes can exceed its repetition limits. Its native endpoint can still
        // enforce the important object shape after those costly bounds are removed; the control
        // plane validates the complete schema before accepting the result.
        var schemaInstruction = $"""
            {request.SystemInstruction}

            Return only one JSON object matching this JSON Schema. Do not use Markdown fences.
            {request.ResponseSchema.GetRawText()}
            """;
        var body = new
        {
            model = request.Model,
            messages = new object[]
            {
                new { role = "system", content = schemaInstruction },
                new { role = "user", content = request.Input },
            },
            stream = false,
            format = OllamaGrammarSchema(request.ResponseSchema),
            options = new
            {
                temperature = 0,
                num_predict = request.MaximumOutputTokens,
            },
        };
        using var response = await httpClient.PostAsJsonAsync(
            OllamaEndpoint(settings.AiBaseUrl, "api/chat"), body, JsonOptions,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        return document.RootElement.GetProperty("message").GetProperty("content").GetString();
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        var settings = configuration.Snapshot();
        if (!settings.AiEnabled) return false;
        try
        {
            using var response = await httpClient.GetAsync(
                IsOllama(settings.AiProvider)
                    ? OllamaEndpoint(settings.AiBaseUrl, "api/tags")
                    : Endpoint(settings.AiBaseUrl, "models"), cancellationToken);
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

    private static Uri OllamaEndpoint(string baseUrl, string path)
    {
        var source = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        var builder = new UriBuilder(source);
        var rootPath = builder.Path.TrimEnd('/');
        if (rootPath.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            rootPath = rootPath[..^3];
        builder.Path = $"{rootPath.TrimEnd('/')}/{path.TrimStart('/')}";
        builder.Query = string.Empty;
        builder.Fragment = string.Empty;
        return builder.Uri;
    }

    private static bool IsOllama(string provider) =>
        string.Equals(provider, "ollama", StringComparison.OrdinalIgnoreCase);

    private static JsonElement OllamaGrammarSchema(JsonElement schema)
    {
        var root = JsonNode.Parse(schema.GetRawText())
            ?? throw new LocalAiException("response-schema-invalid");
        RemoveExpensiveGrammarBounds(root);
        return JsonSerializer.SerializeToElement(root, JsonOptions);
    }

    private static void RemoveExpensiveGrammarBounds(JsonNode node)
    {
        if (node is JsonObject value)
        {
            foreach (var key in ExpensiveGrammarKeywords) value.Remove(key);
            foreach (var child in value.Select(item => item.Value).Where(item => item is not null))
                RemoveExpensiveGrammarBounds(child!);
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.Where(item => item is not null))
                RemoveExpensiveGrammarBounds(child!);
        }
    }

    private static readonly string[] ExpensiveGrammarKeywords =
    [
        "minLength", "maxLength", "pattern", "format",
        "uniqueItems", "contains", "minContains", "maxContains",
        "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf",
        "minProperties", "maxProperties",
    ];
}

public sealed class LocalAiException(string safeCode, Exception? inner = null)
    : Exception(safeCode, inner)
{
    public string SafeCode { get; } = safeCode;
}
