using Alven.Bridge.Configuration;
using Alven.Bridge.ControlPlane;
using Alven.Bridge.Jobs;
using Alven.Bridge.Security;

namespace Alven.Bridge.Runtime;

internal sealed class BridgeWorker(
    IInstallationCredentialStore credentialStore,
    IBridgeControlPlaneClient controlPlane,
    IBridgeJobProcessor processor,
    IBridgeJobReceiptStore receiptStore,
    BridgeRuntimeState runtimeState,
    BridgeRuntimeConfiguration configuration,
    ILogger<BridgeWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> ControlPlaneUnavailable =
        LoggerMessage.Define(LogLevel.Warning, new EventId(1, "ControlPlaneUnavailable"),
            "Bridge control plane is unavailable; retrying without job content.");
    private DateTimeOffset nextHeartbeatAt;
    private DateTimeOffset nextReceiptPruneAt;

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
            await Task.Delay(TimeSpan.FromSeconds(
                configuration.Snapshot().PollIntervalSeconds), stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = configuration.Snapshot();
        if (nextReceiptPruneAt <= DateTimeOffset.UtcNow)
        {
            await receiptStore.PruneExpiredAsync(
                DateTimeOffset.UtcNow.AddDays(-settings.ReceiptRetentionDays), cancellationToken);
            nextReceiptPruneAt = DateTimeOffset.UtcNow.AddHours(1);
        }
        var credential = await credentialStore.ReadAsync(cancellationToken);
        if (credential is null || string.IsNullOrWhiteSpace(settings.ControlPlaneBaseUrl)) return;
        if (nextHeartbeatAt <= DateTimeOffset.UtcNow)
        {
            await controlPlane.SendHeartbeatAsync(credential,
                new BridgeHeartbeatRequest(runtimeState.Version, runtimeState.Capabilities,
                    DateTimeOffset.UtcNow), cancellationToken);
            runtimeState.ReportControlPlaneContact(DateTimeOffset.UtcNow);
            nextHeartbeatAt = DateTimeOffset.UtcNow.AddSeconds(
                settings.HeartbeatIntervalSeconds);
        }

        var job = await controlPlane.PollJobAsync(credential, cancellationToken);
        runtimeState.ReportControlPlaneContact(DateTimeOffset.UtcNow);
        if (job is null)
        {
            runtimeState.ReportHealthy();
            return;
        }
        BridgeJobProcessingResult? result;
        try
        {
            result = await receiptStore.ReadAsync(job, cancellationToken);
        }
        catch (BridgeJobReceiptException exception)
        {
            result = new BridgeJobProcessingResult("rejected", null, exception.SafeCode);
        }
        if (result is null)
        {
            using var lease = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var remaining = job.ExpiresAt - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero) lease.CancelAfter(remaining);
            result = await processor.ProcessAsync(job, lease.Token);
            await receiptStore.SaveAsync(job, result, cancellationToken);
        }
        await controlPlane.CompleteJobAsync(credential, job.JobId,
            new BridgeJobCompletionRequest(job.LeaseToken, result.Outcome, result.Result,
                result.SafeFailureCode, DateTimeOffset.UtcNow), cancellationToken);
        runtimeState.ReportControlPlaneContact(DateTimeOffset.UtcNow);
        await receiptStore.DeleteAsync(job.JobId, cancellationToken);
        if (result.Outcome == "completed") runtimeState.ReportHealthy();
        else runtimeState.ReportFailure(result.SafeFailureCode ?? "job-failed");
    }
}
