using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Alven.Bridge.Capabilities.Ai;
using Alven.Bridge.Capabilities.Storage;
using Alven.Bridge.Administration;
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
        using var readiness = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
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
        var credential = (await store.CreateCandidateAsync(CancellationToken.None)) with
        {
            WorkspaceId = Guid.NewGuid(),
        };

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
        var unsupported = new BridgeJobEnvelope(Guid.NewGuid(), "synthetic-lease", "shell.command",
            payload.RootElement.Clone(), DateTimeOffset.UtcNow.AddMinutes(1));

        var result = await processor.ProcessAsync(unsupported, CancellationToken.None);

        Assert.Equal("rejected", result.Outcome);
        Assert.Equal("capability-unsupported", result.SafeFailureCode);
        Assert.Equal(0, fake.CallCount);
    }

    [Fact]
    public async Task MountedStorageRejectsTraversalAndKeepsContentInsideItsRoot()
    {
        var storageRoot = Path.Combine(stateDirectory, "family-files");
        await using var factory = Factory(storageRoot: storageRoot);
        _ = factory.CreateClient();
        var processor = factory.Services.GetRequiredService<IBridgeJobProcessor>();
        var payload = JsonSerializer.SerializeToElement(new
        {
            relativePath = "../escape.bin",
            contentBase64 = Convert.ToBase64String("safe fixture"u8),
            expectedSha256 = (string?)null,
            overwrite = false,
        });

        var result = await processor.ProcessAsync(new BridgeJobEnvelope(Guid.NewGuid(),
            "synthetic-lease", "storage.write", payload,
            DateTimeOffset.UtcNow.AddMinutes(1)), CancellationToken.None);

        Assert.Equal("failed", result.Outcome);
        Assert.Equal("storage-path-invalid", result.SafeFailureCode);
        Assert.False(File.Exists(Path.Combine(stateDirectory, "escape.bin")));
    }

    [Fact]
    public async Task MountedStorageWritesReadsAndDeletesVerifiedContent()
    {
        var storageRoot = Path.Combine(stateDirectory, "family-files");
        await using var factory = Factory(storageRoot: storageRoot);
        _ = factory.CreateClient();
        var processor = factory.Services.GetRequiredService<IBridgeJobProcessor>();
        var bytes = "private family fixture"u8.ToArray();
        var checksum = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
            .ToLowerInvariant();
        var writePayload = JsonSerializer.SerializeToElement(new
        {
            relativePath = "workspace/content.bin",
            contentBase64 = Convert.ToBase64String(bytes),
            expectedSha256 = checksum,
            overwrite = false,
        });

        var written = await processor.ProcessAsync(new BridgeJobEnvelope(Guid.NewGuid(),
            "write-lease", "storage.write", writePayload,
            DateTimeOffset.UtcNow.AddMinutes(1)), CancellationToken.None);
        var readPayload = JsonSerializer.SerializeToElement(new
        {
            relativePath = "workspace/content.bin",
        });
        var stat = await processor.ProcessAsync(new BridgeJobEnvelope(Guid.NewGuid(),
            "stat-lease", "storage.stat", readPayload,
            DateTimeOffset.UtcNow.AddMinutes(1)), CancellationToken.None);
        var read = await processor.ProcessAsync(new BridgeJobEnvelope(Guid.NewGuid(),
            "read-lease", "storage.read", readPayload,
            DateTimeOffset.UtcNow.AddMinutes(1)), CancellationToken.None);
        var deleted = await processor.ProcessAsync(new BridgeJobEnvelope(Guid.NewGuid(),
            "delete-lease", "storage.delete", readPayload,
            DateTimeOffset.UtcNow.AddMinutes(1)), CancellationToken.None);

        Assert.True(written.Outcome == "completed", written.SafeFailureCode);
        Assert.Equal("completed", stat.Outcome);
        Assert.Equal(checksum, stat.Result?.GetProperty("sha256").GetString());
        Assert.Equal(JsonValueKind.Null,
            stat.Result?.GetProperty("contentBase64").ValueKind);
        Assert.Equal("completed", read.Outcome);
        Assert.Equal(checksum, read.Result?.GetProperty("sha256").GetString());
        Assert.Equal(bytes, Convert.FromBase64String(
            read.Result?.GetProperty("contentBase64").GetString() ?? string.Empty));
        Assert.Equal("completed", deleted.Outcome);
        Assert.False(File.Exists(Path.Combine(storageRoot, "workspace", "content.bin")));
    }

    [Fact]
    public async Task MountedStorageRejectsOversizeBeforeCreatingPermanentFile()
    {
        var storageRoot = Path.Combine(stateDirectory, "bounded-files");
        await using var factory = Factory(storageRoot: storageRoot,
            storageMaximumFileBytes: 1_000_000);
        _ = factory.CreateClient();
        var processor = factory.Services.GetRequiredService<IBridgeJobProcessor>();
        var bytes = new byte[1_000_001];
        var payload = JsonSerializer.SerializeToElement(new
        {
            relativePath = "workspace/too-large.bin",
            contentBase64 = Convert.ToBase64String(bytes),
            expectedSha256 = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
        });

        var result = await processor.ProcessAsync(new BridgeJobEnvelope(Guid.NewGuid(),
            "oversize-lease", "storage.write", payload,
            DateTimeOffset.UtcNow.AddMinutes(1)), CancellationToken.None);

        Assert.Equal("failed", result.Outcome);
        Assert.Equal("storage-file-too-large", result.SafeFailureCode);
        Assert.False(File.Exists(Path.Combine(storageRoot, "workspace", "too-large.bin")));
    }

    [Fact]
    public async Task ReadOnlyMountRejectsWritesWithoutChangingExistingContent()
    {
        var storageRoot = Path.Combine(stateDirectory, "read-only-files");
        await using (var initializer = Factory(storageRoot: storageRoot))
        {
            _ = initializer.CreateClient();
            Assert.True(await initializer.Services.GetRequiredService<ILocalStorageClient>()
                .IsHealthyAsync(CancellationToken.None));
        }
        var existingPath = Path.Combine(storageRoot, "existing.bin");
        await File.WriteAllTextAsync(existingPath, "unchanged");
        await using var factory = Factory(storageRoot: storageRoot, storageReadOnly: true);
        _ = factory.CreateClient();
        var payload = JsonSerializer.SerializeToElement(new
        {
            relativePath = "existing.bin",
            contentBase64 = Convert.ToBase64String("changed"u8),
        });

        var result = await factory.Services.GetRequiredService<IBridgeJobProcessor>()
            .ProcessAsync(new BridgeJobEnvelope(Guid.NewGuid(), "readonly-lease",
                "storage.write", payload, DateTimeOffset.UtcNow.AddMinutes(1)),
                CancellationToken.None);

        Assert.Equal("failed", result.Outcome);
        Assert.Equal("storage-read-only", result.SafeFailureCode);
        Assert.Equal("unchanged", await File.ReadAllTextAsync(existingPath));
    }

    [Fact]
    public async Task CompletedJobReceiptSurvivesBridgeRestart()
    {
        var jobId = Guid.NewGuid();
        using var payload = JsonDocument.Parse("{}");
        var job = new BridgeJobEnvelope(jobId, "lease", "ai.openai-compatible",
            payload.RootElement.Clone(), DateTimeOffset.UtcNow.AddMinutes(1));
        var result = new BridgeJobProcessingResult("completed",
            JsonSerializer.SerializeToElement(new { safe = true }), null);
        await using (var first = Factory())
        {
            _ = first.CreateClient();
            await first.Services.GetRequiredService<IBridgeJobReceiptStore>()
                .SaveAsync(job, result, CancellationToken.None);
        }
        await using var restarted = Factory();
        _ = restarted.CreateClient();

        var restored = await restarted.Services.GetRequiredService<IBridgeJobReceiptStore>()
            .ReadAsync(job, CancellationToken.None);

        Assert.NotNull(restored);
        Assert.Equal("completed", restored.Outcome);
        Assert.True(restored.Result?.GetProperty("safe").GetBoolean());
    }

    [Fact]
    public async Task ReusedJobIdWithChangedPayloadCannotReplayAReceipt()
    {
        await using var factory = Factory();
        _ = factory.CreateClient();
        var receipts = factory.Services.GetRequiredService<IBridgeJobReceiptStore>();
        var jobId = Guid.NewGuid();
        var original = new BridgeJobEnvelope(jobId, "lease-1", "storage.delete",
            JsonSerializer.SerializeToElement(new { relativePath = "one.bin" }),
            DateTimeOffset.UtcNow.AddMinutes(1));
        await receipts.SaveAsync(original, new BridgeJobProcessingResult("completed",
            null, null), CancellationToken.None);
        var changed = original with
        {
            LeaseToken = "lease-2",
            Payload = JsonSerializer.SerializeToElement(new { relativePath = "two.bin" }),
        };

        var exception = await Assert.ThrowsAsync<BridgeJobReceiptException>(() =>
            receipts.ReadAsync(changed, CancellationToken.None));

        Assert.Equal("job-replay-mismatch", exception.SafeCode);
    }

    [Fact]
    public async Task ExpiredJobReceiptsArePrunedWithoutTouchingCurrentReceipts()
    {
        await using var factory = Factory();
        _ = factory.CreateClient();
        var receipts = factory.Services.GetRequiredService<IBridgeJobReceiptStore>();
        using var payload = JsonDocument.Parse("{}");
        var oldJob = new BridgeJobEnvelope(Guid.NewGuid(), "old", "ai.openai-compatible",
            payload.RootElement.Clone(), DateTimeOffset.UtcNow.AddMinutes(1));
        var currentJob = oldJob with { JobId = Guid.NewGuid(), LeaseToken = "current" };
        var result = new BridgeJobProcessingResult("completed", null, null);
        await receipts.SaveAsync(oldJob, result, CancellationToken.None);
        await receipts.SaveAsync(currentJob, result, CancellationToken.None);
        File.SetLastWriteTimeUtc(Path.Combine(stateDirectory, "job-receipts",
            $"{oldJob.JobId:D}.json"), DateTime.UtcNow.AddDays(-31));

        var removed = await receipts.PruneExpiredAsync(DateTimeOffset.UtcNow.AddDays(-30),
            CancellationToken.None);

        Assert.Equal(1, removed);
        Assert.Null(await receipts.ReadAsync(oldJob, CancellationToken.None));
        Assert.NotNull(await receipts.ReadAsync(currentJob, CancellationToken.None));
    }

    [Fact]
    public async Task AdministrationRejectsNonLocalHost()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://bridge.example.invalid"),
        });

        using var response = await client.GetAsync("/api/v1/setup/session");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ChangedStorageMountFailsClosed()
    {
        var storageRoot = Path.Combine(stateDirectory, "changed-mount");
        await using var factory = Factory(storageRoot: storageRoot);
        _ = factory.CreateClient();
        var storage = factory.Services.GetRequiredService<Alven.Bridge.Capabilities.Storage.ILocalStorageClient>();
        Assert.True(await storage.IsHealthyAsync(CancellationToken.None));
        File.Delete(Path.Combine(storageRoot, ".alven-bridge-mount-id"));
        var payload = JsonSerializer.SerializeToElement(new { relativePath = "file.bin" });

        var result = await factory.Services.GetRequiredService<IBridgeJobProcessor>()
            .ProcessAsync(new BridgeJobEnvelope(Guid.NewGuid(), "lease", "storage.read",
                payload, DateTimeOffset.UtcNow.AddMinutes(1)), CancellationToken.None);

        Assert.Equal("failed", result.Outcome);
        Assert.Equal("storage-mount-changed", result.SafeFailureCode);
        Assert.False(await storage.IsHealthyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DiagnosticBundleIsContentRedacted()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var diagnostic = await client.GetFromJsonAsync<BridgeDiagnostics>(
            "/api/v1/diagnostics");
        var serialized = JsonSerializer.Serialize(diagnostic);

        Assert.NotNull(diagnostic);
        Assert.Contains("No prompts", diagnostic.RedactionNotice, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-model", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("11434", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(stateDirectory, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WizardIsServedLocallyAndConfigurationWritesRequireItsNonce()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/");
        using var denied = await client.PutAsJsonAsync("/api/v1/setup/configuration", new
        {
            controlPlaneBaseUrl = "https://api.example.invalid",
            pollIntervalSeconds = 10,
            heartbeatIntervalSeconds = 30,
            aiEnabled = false,
            aiProvider = "ollama",
            aiBaseUrl = "http://host.docker.internal:11434/v1/",
            aiAllowedModels = Array.Empty<string>(),
            storageEnabled = false,
            storageRootPath = "/data/family-files",
            storageReadOnly = false,
            storageMaximumFileBytes = 5_000_000,
            receiptRetentionDays = 30,
        });

        Assert.Contains("Your home stays yours", html, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        if (Directory.Exists(stateDirectory)) Directory.Delete(stateDirectory, true);
        return Task.CompletedTask;
    }

    private WebApplicationFactory<Program> Factory(Action<IServiceCollection>? configure = null,
        string? storageRoot = null, bool storageReadOnly = false,
        long storageMaximumFileBytes = 5_000_000) =>
        new BridgeFactory(stateDirectory, storageRoot, storageReadOnly,
            storageMaximumFileBytes, configure);

    private sealed class BridgeFactory(
        string stateDirectory,
        string? storageRoot,
        bool storageReadOnly,
        long storageMaximumFileBytes,
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
                    ["Bridge:Storage:Enabled"] = storageRoot is null ? "false" : "true",
                    ["Bridge:Storage:RootPath"] = storageRoot ?? Path.Combine(stateDirectory, "storage"),
                    ["Bridge:Storage:ReadOnly"] = storageReadOnly.ToString(),
                    ["Bridge:Storage:MaximumFileBytes"] = storageMaximumFileBytes.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
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
