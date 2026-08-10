using System.Net;
using System.Text;
using Alven.Bridge.Capabilities.Storage;
using Alven.Bridge.Configuration;

namespace Alven.Bridge.Tests;

public sealed class RemoteStorageClientTests
{
    [Fact]
    public async Task WebDavWriteUsesConfiguredRootAndBasicAuthentication()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var client = new RemoteStorageClient(new HttpClient(handler));
        var bytes = Encoding.UTF8.GetBytes("family-file");
        var request = new LocalStorageJobRequest("workspace/content.bin",
            Convert.ToBase64String(bytes), Sha256(bytes));

        var result = await client.ProcessAsync("storage.write", request,
            Settings("webdav"), CancellationToken.None);

        Assert.Equal(Sha256(bytes), result.Sha256);
        var write = handler.Requests.Last();
        Assert.Equal(HttpMethod.Put, write.Method);
        Assert.EndsWith("/alven/workspace/content.bin", write.Uri.AbsolutePath,
            StringComparison.Ordinal);
        Assert.StartsWith("Basic ", write.Authorization, StringComparison.Ordinal);
    }

    [Fact]
    public async Task S3WriteIsSignedWithoutPuttingCredentialsInTheUrl()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new RemoteStorageClient(new HttpClient(handler));
        var bytes = Encoding.UTF8.GetBytes("family-file");

        await client.ProcessAsync("storage.write", new LocalStorageJobRequest(
            "workspace/content.bin", Convert.ToBase64String(bytes), Sha256(bytes)),
            Settings("s3"), CancellationToken.None);

        var write = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, write.Method);
        Assert.EndsWith("/family-files/alven/workspace/content.bin", write.Uri.AbsolutePath,
            StringComparison.Ordinal);
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=access-key/", write.Authorization,
            StringComparison.Ordinal);
        Assert.DoesNotContain("secret-key", write.Uri.ToString(), StringComparison.Ordinal);
    }

    private static BridgeEditableSettings Settings(string provider) => new(
        "https://api.example.test", 10, 30, false, "ollama",
        "http://127.0.0.1:11434/v1/", [], true, provider, "/data/family-files",
        "https://storage.example.test/root", "family-files", "alven", "family-user",
        "family-password", "access-key", "secret-key", "us-east-1", false,
        5_000_000, 30);

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
            .ToLowerInvariant();

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new(request.Method, request.RequestUri!,
                request.Headers.TryGetValues("Authorization", out var authorization)
                    ? authorization.Single() : string.Empty, body));
            return responseFactory(request);
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri,
        string Authorization, string? Body);
}
