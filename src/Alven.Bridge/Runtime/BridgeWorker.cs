using Alven.Bridge.Configuration;
using Alven.Bridge.ControlPlane;
using Alven.Bridge.Jobs;
using Alven.Bridge.Security;
using Microsoft.Extensions.Options;

namespace Alven.Bridge.Runtime;

internal sealed class BridgeWorker(
    IInstallationCredentialStore credentialStore,
    IBridgeControlPlaneClient controlPlane,
    IBridgeJobProcessor processor,
    BridgeRuntimeState runtimeState,
    IOptions<BridgeOptions> options,
    ILogger<BridgeWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> ControlPlaneUnavailable =
        LoggerMessage.Define(LogLevel.Warning, new EventId(1, "ControlPlaneUnavailable"),
            "Bridge control plane is unavailable; retrying without job content.");
    private DateTimeOffset nextHeartbeatAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is HttpRequestException
                or InvalidOperationException or InvalidDataException)
            {
                runtimeState.ReportFailure("control-plane-unavailable");
                ControlPlaneUnavailable(logger, exception);
            }
            await Task.Delay(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds), stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var credential = await credentialStore.ReadAsync(cancellationToken);
        if (credential is null || string.IsNullOrWhiteSpace(options.Value.ControlPlaneBaseUrl)) return;
        if (nextHeartbeatAt <= DateTimeOffset.UtcNow)
        {
            await controlPlane.SendHeartbeatAsync(credential,
                new BridgeHeartbeatRequest(runtimeState.Version, runtimeState.Capabilities,
                    DateTimeOffset.UtcNow), cancellationToken);
            nextHeartbeatAt = DateTimeOffset.UtcNow.AddSeconds(
                options.Value.HeartbeatIntervalSeconds);
        }

        var job = await controlPlane.PollJobAsync(credential, cancellationToken);
        if (job is null)
        {
            runtimeState.ReportHealthy();
            return;
        }
        var result = await processor.ProcessAsync(job, cancellationToken);
        await controlPlane.CompleteJobAsync(credential, job.JobId,
            new BridgeJobCompletionRequest(job.LeaseToken, result.Outcome, result.Result,
                result.SafeFailureCode, DateTimeOffset.UtcNow), cancellationToken);
        if (result.Outcome == "completed") runtimeState.ReportHealthy();
        else runtimeState.ReportFailure(result.SafeFailureCode ?? "job-failed");
    }
}
