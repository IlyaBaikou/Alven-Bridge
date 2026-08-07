using System.Net;
using System.Text;
using System.Text.Json;
using Alven.Bridge.Capabilities.Ai;
using Alven.Bridge.Configuration;
using Microsoft.Extensions.Options;

namespace Alven.Bridge.Tests;

public sealed class LocalAiClientTests : IDisposable
{
    private readonly string stateDirectory = Path.Combine(Path.GetTempPath(),
        $"alven-local-ai-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task OllamaUsesNativeJsonModeWithoutCompilingTheResponseSchema()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK,
            "{\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"answer\\\":\\\"ok\\\"}\"},\"done\":true}"));
        using var configuration = Configuration("ollama", "http://localhost:11434/v1/");
        var client = new OpenAiCompatibleLocalAiClient(new HttpClient(handler), configuration);
        using var schema = JsonDocument.Parse("""
            {"type":"object","properties":{"answer":{"type":"string","maxLength":2000}},"required":["answer"]}
            """);

        var result = await client.CompleteAsync(Request(schema.RootElement.Clone()),
            CancellationToken.None);

        Assert.Equal("{\"answer\":\"ok\"}", result.Json);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("/api/chat", sent.Uri.AbsolutePath);
        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal("json", body.RootElement.GetProperty("format").GetString());
        Assert.False(body.RootElement.TryGetProperty("response_format", out _));
        Assert.Contains("maxLength", body.RootElement.GetProperty("messages")[0]
            .GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenericProviderKeepsOpenAiJsonSchemaContract()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK,
            "{\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"ok\\\"}\"}}]}"));
        using var configuration = Configuration("lm-studio", "http://localhost:1234/v1/");
        var client = new OpenAiCompatibleLocalAiClient(new HttpClient(handler), configuration);
        using var schema = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}}}");

        await client.CompleteAsync(Request(schema.RootElement.Clone()), CancellationToken.None);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("/v1/chat/completions", sent.Uri.AbsolutePath);
        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal("json_schema", body.RootElement.GetProperty("response_format")
            .GetProperty("type").GetString());
    }

    private BridgeRuntimeConfiguration Configuration(string provider, string baseUrl) => new(
        Options.Create(new BridgeOptions
        {
            StateDirectory = stateDirectory,
            ControlPlaneBaseUrl = "https://api.example.test",
            Ai = new LocalAiOptions
            {
                Enabled = true,
                Provider = provider,
                BaseUrl = baseUrl,
                AllowedModels = ["test-model"],
            },
        }));

    private static LocalAiJobRequest Request(JsonElement schema) => new(
        "ai.openai-compatible", "test-model", "Answer from the supplied source.",
        "What is covered?", schema, 512, 30);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    public void Dispose()
    {
        if (Directory.Exists(stateDirectory)) Directory.Delete(stateDirectory, true);
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new(request.RequestUri!, body));
            return responseFactory(request);
        }
    }

    private sealed record RecordedRequest(Uri Uri, string Body);
}
