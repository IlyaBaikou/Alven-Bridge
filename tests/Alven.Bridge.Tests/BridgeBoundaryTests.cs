using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Alven.Bridge.Capabilities.Ai;
using Alven.Bridge.Configuration;
using Alven.Bridge.ControlPlane;
using Alven.Bridge.Jobs;
using Alven.Bridge.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Alven.Bridge.Tests;

public sealed class BridgeBoundaryTests : IAsyncLifetime
{
    private readonly string stateDirectory = Path.Combine(Path.GetTempPath(),
        $"alven-bridge-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task StartsUnpairedAndExposesOnlySafeStatus()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        using var health = await client.GetAsync("/health/live");
        var status = await client.GetFromJsonAsync<BridgeRuntimeStatus>("/api/v1/status");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.NotNull(status);
        Assert.False(status.Paired);
        Assert.Null(status.InstallationId);
        Assert.Contains("ai.openai-compatible", status.Capabilities);
    }

    [Fact]
    public void RejectsAnInsecureRemoteControlPlane()
    {
        var errors = BridgeOptionsRules.Validate(new BridgeOptions
        {
            ControlPlaneBaseUrl = "http://remote.example.invalid",
        });

        Assert.Contains(errors, item => item.Contains("must use HTTPS", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallationCredentialUsesProtectedStateFile()
    {
        await using var factory = Factory();
        _ = factory.CreateClient();
        var store = factory.Services.GetRequiredService<IInstallationCredentialStore>();
        var credential = await store.CreateCandidateAsync(CancellationToken.None);

        await store.SaveAsync(credential, CancellationToken.None);
        var restored = await store.ReadAsync(CancellationToken.None);

        Assert.Equal(credential, restored);
        var path = Path.Combine(stateDirectory, "installation.json");
        Assert.True(File.Exists(path));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(path));
        }
    }

    [Fact]
    public async Task UnsupportedOrMismatchedJobsNeverReachLocalAi()
    {
        var fake = new FakeLocalAiClient();
        await using var factory = Factory(services =>
        {
            services.RemoveAll<ILocalAiClient>();
            services.AddSingleton<ILocalAiClient>(fake);
        });
        _ = factory.CreateClient();
        var processor = factory.Services.GetRequiredService<IBridgeJobProcessor>();
        using var payload = JsonDocument.Parse("{}");
        var unsupported = new BridgeJobEnvelope(Guid.NewGuid(), "synthetic-lease", "storage.write",
            payload.RootElement.Clone(), DateTimeOffset.UtcNow.AddMinutes(1));

        var result = await processor.ProcessAsync(unsupported, CancellationToken.None);

        Assert.Equal("rejected", result.Outcome);
        Assert.Equal("capability-unsupported", result.SafeFailureCode);
        Assert.Equal(0, fake.CallCount);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        if (Directory.Exists(stateDirectory)) Directory.Delete(stateDirectory, true);
        return Task.CompletedTask;
    }

    private WebApplicationFactory<Program> Factory(Action<IServiceCollection>? configure = null) =>
        new BridgeFactory(stateDirectory, configure);

    private sealed class BridgeFactory(
        string stateDirectory,
        Action<IServiceCollection>? configure) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Bridge:StateDirectory", stateDirectory);
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Bridge:StateDirectory"] = stateDirectory,
                    ["Bridge:Ai:AllowedModels:0"] = "synthetic-model",
                });
            });
            if (configure is not null) builder.ConfigureServices(configure);
        }
    }

    private sealed class FakeLocalAiClient : ILocalAiClient
    {
        public int CallCount { get; private set; }

        public Task<LocalAiJobResult> CompleteAsync(LocalAiJobRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new LocalAiJobResult("{}", "synthetic", request.Model));
        }

        public Task<bool> IsHealthyAsync(CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }
}
