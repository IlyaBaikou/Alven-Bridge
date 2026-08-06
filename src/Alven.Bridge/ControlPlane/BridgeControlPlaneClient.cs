using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Alven.Bridge.Configuration;
using Alven.Bridge.Security;
using Microsoft.Extensions.Options;

namespace Alven.Bridge.ControlPlane;

public interface IBridgeControlPlaneClient
{
    Task<PairInstallationResponse> PairAsync(PairInstallationRequest request,
        CancellationToken cancellationToken);
    Task<string> GetAccessTokenAsync(InstallationCredential credential,
        CancellationToken cancellationToken);
    Task SendHeartbeatAsync(InstallationCredential credential, BridgeHeartbeatRequest request,
        CancellationToken cancellationToken);
    Task<BridgeJobEnvelope?> PollJobAsync(InstallationCredential credential,
        CancellationToken cancellationToken);
    Task CompleteJobAsync(InstallationCredential credential, Guid jobId,
        BridgeJobCompletionRequest request, CancellationToken cancellationToken);
}

internal sealed class BridgeControlPlaneClient(
    HttpClient httpClient,
    IOptions<BridgeOptions> options) : IBridgeControlPlaneClient, IDisposable
{
    private readonly SemaphoreSlim tokenLock = new(1, 1);
    private string? accessToken;
    private DateTimeOffset accessTokenExpiresAt;

    public async Task<PairInstallationResponse> PairAsync(PairInstallationRequest request,
        CancellationToken cancellationToken)
    {
        ConfigureBaseAddress();
        using var response = await httpClient.PostAsJsonAsync(
            "api/v1/bridge/pairings/exchange", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PairInstallationResponse>(cancellationToken)
            ?? throw new InvalidDataException("The pairing response was empty.");
    }

    public async Task<string> GetAccessTokenAsync(InstallationCredential credential,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(accessToken)
            && accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return accessToken;
        await tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(accessToken)
                && accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return accessToken;
            ConfigureBaseAddress();
            using var response = await httpClient.PostAsJsonAsync(
                "api/v1/bridge/installations/token",
                new IssueInstallationTokenRequest(credential.InstallationId, credential.Secret),
                cancellationToken);
            response.EnsureSuccessStatusCode();
            var token = await response.Content.ReadFromJsonAsync<IssueInstallationTokenResponse>(
                cancellationToken) ?? throw new InvalidDataException("The token response was empty.");
            accessToken = token.AccessToken;
            accessTokenExpiresAt = token.ExpiresAt;
            return token.AccessToken;
        }
        finally
        {
            tokenLock.Release();
        }
    }

    public async Task SendHeartbeatAsync(InstallationCredential credential,
        BridgeHeartbeatRequest request, CancellationToken cancellationToken)
    {
        using var message = await AuthorizedAsync(credential, HttpMethod.Post,
            $"api/v1/bridge/installations/{credential.InstallationId:D}/heartbeat",
            JsonContent.Create(request), cancellationToken);
        using var response = await httpClient.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<BridgeJobEnvelope?> PollJobAsync(InstallationCredential credential,
        CancellationToken cancellationToken)
    {
        using var message = await AuthorizedAsync(credential, HttpMethod.Get,
            $"api/v1/bridge/installations/{credential.InstallationId:D}/jobs/next",
            null, cancellationToken);
        using var response = await httpClient.SendAsync(message,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BridgeJobEnvelope>(cancellationToken)
            ?? throw new InvalidDataException("The job response was empty.");
    }

    public async Task CompleteJobAsync(InstallationCredential credential, Guid jobId,
        BridgeJobCompletionRequest request, CancellationToken cancellationToken)
    {
        using var message = await AuthorizedAsync(credential, HttpMethod.Post,
            $"api/v1/bridge/installations/{credential.InstallationId:D}/jobs/{jobId:D}/complete",
            JsonContent.Create(request), cancellationToken);
        using var response = await httpClient.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpRequestMessage> AuthorizedAsync(InstallationCredential credential,
        HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        ConfigureBaseAddress();
        var token = await GetAccessTokenAsync(credential, cancellationToken);
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private void ConfigureBaseAddress()
    {
        var configured = options.Value.ControlPlaneBaseUrl.Trim();
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("The Alven control plane is not configured.");
        httpClient.BaseAddress ??= new Uri(configured.TrimEnd('/') + "/", UriKind.Absolute);
    }

    public void Dispose() => tokenLock.Dispose();
}
