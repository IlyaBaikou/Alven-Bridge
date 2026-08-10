using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Alven.Bridge.Configuration;

namespace Alven.Bridge.Capabilities.Storage;

internal sealed class RemoteStorageClient(HttpClient httpClient)
{
    public async Task<LocalStorageJobResult> ProcessAsync(string capability,
        LocalStorageJobRequest request, BridgeEditableSettings settings,
        CancellationToken cancellationToken)
    {
        var key = SafeKey(settings.StoragePrefix, request.RelativePath);
        return settings.StorageProvider switch
        {
            "webdav" => await ProcessWebDavAsync(capability, key, request, settings,
                cancellationToken),
            "s3" => await ProcessS3Async(capability, key, request, settings,
                cancellationToken),
            _ => throw new LocalStorageException("storage-provider-unsupported"),
        };
    }

    public async Task<bool> IsHealthyAsync(BridgeEditableSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = settings.StorageProvider == "webdav"
                ? WebDavRequest(new HttpMethod("PROPFIND"), WebDavUri(settings, string.Empty),
                    settings, null)
                : S3Request(HttpMethod.Head, S3Uri(settings, string.Empty), settings,
                    [], DateTimeOffset.UtcNow);
            if (settings.StorageProvider == "webdav")
                request.Headers.TryAddWithoutValidation("Depth", "0");
            using var response = await httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode
                || settings.StorageProvider == "webdav"
                    && response.StatusCode == HttpStatusCode.MultiStatus;
        }
        catch (Exception exception) when (exception is HttpRequestException
            or TaskCanceledException or UriFormatException)
        {
            return false;
        }
    }

    private async Task<LocalStorageJobResult> ProcessWebDavAsync(string capability,
        string key, LocalStorageJobRequest request, BridgeEditableSettings settings,
        CancellationToken cancellationToken)
    {
        var uri = WebDavUri(settings, key);
        return capability switch
        {
            "storage.stat" => await ReadRemoteAsync(uri, request.RelativePath, settings,
                false, cancellationToken),
            "storage.read" => await ReadRemoteAsync(uri, request.RelativePath, settings,
                true, cancellationToken),
            "storage.write" when !settings.StorageReadOnly => await WriteWebDavAsync(uri,
                key, request, settings, cancellationToken),
            "storage.delete" when !settings.StorageReadOnly => await DeleteWebDavAsync(uri,
                request.RelativePath, settings, cancellationToken),
            "storage.write" or "storage.delete" =>
                throw new LocalStorageException("storage-read-only"),
            _ => throw new LocalStorageException("storage-capability-unsupported"),
        };
    }

    private async Task<LocalStorageJobResult> ReadRemoteAsync(Uri uri, string relativePath,
        BridgeEditableSettings settings, bool includeContent,
        CancellationToken cancellationToken)
    {
        using var request = WebDavRequest(HttpMethod.Get, uri, settings, null);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new(relativePath, 0, string.Empty, Missing: true);
        EnsureSuccess(response, "webdav-read-failed");
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        EnsureSize(bytes.LongLength, settings.StorageMaximumFileBytes);
        return new(relativePath, bytes.LongLength, Sha256(bytes),
            includeContent ? Convert.ToBase64String(bytes) : null);
    }

    private async Task<LocalStorageJobResult> WriteWebDavAsync(Uri uri, string key,
        LocalStorageJobRequest request, BridgeEditableSettings settings,
        CancellationToken cancellationToken)
    {
        var bytes = DecodeAndVerify(request, settings.StorageMaximumFileBytes);
        await EnsureWebDavFoldersAsync(settings, key, cancellationToken);
        using var message = WebDavRequest(HttpMethod.Put, uri, settings, bytes);
        if (!request.Overwrite) message.Headers.TryAddWithoutValidation("If-None-Match", "*");
        using var response = await httpClient.SendAsync(message, cancellationToken);
        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
            throw new LocalStorageException("storage-file-exists");
        EnsureSuccess(response, "webdav-write-failed");
        return new(request.RelativePath, bytes.LongLength, Sha256(bytes));
    }

    private async Task EnsureWebDavFoldersAsync(BridgeEditableSettings settings, string key,
        CancellationToken cancellationToken)
    {
        var segments = key.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var count = 1; count < segments.Length; count++)
        {
            var folder = string.Join('/', segments.Take(count));
            using var request = WebDavRequest(new HttpMethod("MKCOL"),
                WebDavUri(settings, folder), settings, null);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode && response.StatusCode is not
                (HttpStatusCode.MethodNotAllowed or HttpStatusCode.Conflict))
                EnsureSuccess(response, "webdav-folder-create-failed");
        }
    }

    private async Task<LocalStorageJobResult> DeleteWebDavAsync(Uri uri,
        string relativePath, BridgeEditableSettings settings,
        CancellationToken cancellationToken)
    {
        using var request = WebDavRequest(HttpMethod.Delete, uri, settings, null);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
            EnsureSuccess(response, "webdav-delete-failed");
        using var verify = WebDavRequest(HttpMethod.Head, uri, settings, null);
        using var verified = await httpClient.SendAsync(verify, cancellationToken);
        if (verified.StatusCode != HttpStatusCode.NotFound)
            throw new LocalStorageException("storage-delete-unverified");
        return new(relativePath, 0, string.Empty, Missing: true);
    }

    private async Task<LocalStorageJobResult> ProcessS3Async(string capability, string key,
        LocalStorageJobRequest request, BridgeEditableSettings settings,
        CancellationToken cancellationToken)
    {
        return capability switch
        {
            "storage.stat" => await ReadS3Async(key, request.RelativePath, settings,
                false, cancellationToken),
            "storage.read" => await ReadS3Async(key, request.RelativePath, settings,
                true, cancellationToken),
            "storage.write" when !settings.StorageReadOnly => await WriteS3Async(key,
                request, settings, cancellationToken),
            "storage.delete" when !settings.StorageReadOnly => await DeleteS3Async(key,
                request.RelativePath, settings, cancellationToken),
            "storage.write" or "storage.delete" =>
                throw new LocalStorageException("storage-read-only"),
            _ => throw new LocalStorageException("storage-capability-unsupported"),
        };
    }

    private async Task<LocalStorageJobResult> ReadS3Async(string key, string relativePath,
        BridgeEditableSettings settings, bool includeContent,
        CancellationToken cancellationToken)
    {
        using var request = S3Request(HttpMethod.Get, S3Uri(settings, key), settings, [],
            DateTimeOffset.UtcNow);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new(relativePath, 0, string.Empty, Missing: true);
        EnsureSuccess(response, "s3-read-failed");
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        EnsureSize(bytes.LongLength, settings.StorageMaximumFileBytes);
        return new(relativePath, bytes.LongLength, Sha256(bytes),
            includeContent ? Convert.ToBase64String(bytes) : null);
    }

    private async Task<LocalStorageJobResult> WriteS3Async(string key,
        LocalStorageJobRequest request, BridgeEditableSettings settings,
        CancellationToken cancellationToken)
    {
        var bytes = DecodeAndVerify(request, settings.StorageMaximumFileBytes);
        using var message = S3Request(HttpMethod.Put, S3Uri(settings, key), settings, bytes,
            DateTimeOffset.UtcNow);
        if (!request.Overwrite) message.Headers.TryAddWithoutValidation("If-None-Match", "*");
        using var response = await httpClient.SendAsync(message, cancellationToken);
        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
            throw new LocalStorageException("storage-file-exists");
        EnsureSuccess(response, "s3-write-failed");
        return new(request.RelativePath, bytes.LongLength, Sha256(bytes));
    }

    private async Task<LocalStorageJobResult> DeleteS3Async(string key, string relativePath,
        BridgeEditableSettings settings, CancellationToken cancellationToken)
    {
        using var request = S3Request(HttpMethod.Delete, S3Uri(settings, key), settings, [],
            DateTimeOffset.UtcNow);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
            EnsureSuccess(response, "s3-delete-failed");
        using var verify = S3Request(HttpMethod.Head, S3Uri(settings, key), settings, [],
            DateTimeOffset.UtcNow);
        using var verified = await httpClient.SendAsync(verify, cancellationToken);
        if (verified.StatusCode != HttpStatusCode.NotFound)
            throw new LocalStorageException("storage-delete-unverified");
        return new(relativePath, 0, string.Empty, Missing: true);
    }

    private static HttpRequestMessage WebDavRequest(HttpMethod method, Uri uri,
        BridgeEditableSettings settings, byte[]? bytes)
    {
        var request = new HttpRequestMessage(method, uri);
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{settings.StorageUsername}:{settings.StoragePassword}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        if (bytes is not null) request.Content = new ByteArrayContent(bytes);
        return request;
    }

    private static HttpRequestMessage S3Request(HttpMethod method, Uri uri,
        BridgeEditableSettings settings, byte[] body, DateTimeOffset now)
    {
        var payloadHash = Sha256(body);
        var amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var date = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var canonicalHeaders = $"host:{uri.Authority}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{amzDate}\n";
        const string signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        var canonicalRequest = $"{method.Method}\n{uri.AbsolutePath}\n{uri.Query.TrimStart('?')}\n"
            + $"{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
        var scope = $"{date}/{settings.StorageRegion}/s3/aws4_request";
        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Sha256(Encoding.UTF8.GetBytes(canonicalRequest))}";
        var dateKey = Hmac(Encoding.UTF8.GetBytes($"AWS4{settings.StorageSecretKey}"), date);
        var regionKey = Hmac(dateKey, settings.StorageRegion);
        var serviceKey = Hmac(regionKey, "s3");
        var signingKey = Hmac(serviceKey, "aws4_request");
        var signature = Convert.ToHexString(HMACSHA256.HashData(signingKey,
            Encoding.UTF8.GetBytes(stringToSign))).ToLowerInvariant();
        var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.TryAddWithoutValidation("Authorization",
            $"AWS4-HMAC-SHA256 Credential={settings.StorageAccessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
        if (method == HttpMethod.Put) request.Content = new ByteArrayContent(body);
        return request;
    }

    private static byte[] Hmac(byte[] key, string value) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));

    private static Uri WebDavUri(BridgeEditableSettings settings, string key) =>
        Combine(settings.StorageEndpoint, key);

    private static Uri S3Uri(BridgeEditableSettings settings, string key) =>
        Combine(settings.StorageEndpoint,
            string.Join('/', new[] { settings.StorageBucket, key }
                .Where(value => !string.IsNullOrWhiteSpace(value))));

    private static Uri Combine(string endpoint, string key)
    {
        var root = new Uri(endpoint.TrimEnd('/') + "/", UriKind.Absolute);
        var encoded = string.Join('/', key.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));
        return new Uri(root, encoded);
    }

    private static string SafeKey(string prefix, string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').Trim('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."
                || segment.StartsWith(".alven-bridge-", StringComparison.Ordinal)))
            throw new LocalStorageException("storage-path-invalid");
        return string.Join('/', new[] { prefix.Trim('/'), string.Join('/', segments) }
            .Where(value => value.Length > 0));
    }

    private static byte[] DecodeAndVerify(LocalStorageJobRequest request, long maximumBytes)
    {
        if (string.IsNullOrWhiteSpace(request.ContentBase64))
            throw new LocalStorageException("storage-content-required");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(request.ContentBase64); }
        catch (FormatException exception)
        { throw new LocalStorageException("storage-content-invalid", exception); }
        EnsureSize(bytes.LongLength, maximumBytes);
        var checksum = Sha256(bytes);
        if (!string.IsNullOrWhiteSpace(request.ExpectedSha256)
            && !string.Equals(checksum, request.ExpectedSha256.Trim(),
                StringComparison.OrdinalIgnoreCase))
            throw new LocalStorageException("storage-checksum-mismatch");
        return bytes;
    }

    private static void EnsureSize(long size, long maximum)
    {
        if (size > maximum) throw new LocalStorageException("storage-file-too-large");
    }

    private static void EnsureSuccess(HttpResponseMessage response, string fallback)
    {
        if (response.IsSuccessStatusCode) return;
        throw new LocalStorageException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "storage-access-denied",
            HttpStatusCode.InsufficientStorage => "storage-quota-exceeded",
            HttpStatusCode.TooManyRequests => "storage-rate-limited",
            _ => fallback,
        });
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
