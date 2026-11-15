using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Nightware.Adapters.Jmap.Core.Auth;
using Nightware.Adapters.Jmap.Core.Errors;
using Nightware.Adapters.Jmap.Core.Http;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Session;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Core.Blobs;

/// <summary>A blob stored by an upload (RFC 8620 §6.1).</summary>
internal sealed record UploadedBlob(string AccountId, string BlobId, string Type, long Size);

/// <summary>Binary upload/download (RFC 8620 §6). Scoped; uses the caller's credential.</summary>
internal interface IJmapBlobService
{
    /// <summary>Streams <paramref name="content"/> to the account's upload URL. The stream is not buffered.</summary>
    Task<Result<UploadedBlob>> UploadAsync(string accountId, Stream content, string contentType, CancellationToken cancellationToken);

    /// <summary>
    /// Starts a download and returns once the response headers arrive (so 404/403 surface as
    /// errors). The body is never buffered: the first <see cref="BlobContent.Open"/> hands out the
    /// response stream itself; later calls download again.
    /// </summary>
    /// <param name="name">File name for the download URL and <see cref="BlobContent.FileName"/>.</param>
    /// <param name="contentType">Type to ask the server to label the blob with (<c>{type}</c>).</param>
    Task<Result<BlobContent>> DownloadAsync(string accountId, string blobId, string? name, string contentType, CancellationToken cancellationToken);
}

/// <inheritdoc />
internal sealed class JmapBlobService(JmapConnectionProvider connections, IHttpClientFactory httpClientFactory, TimeProvider time) : IJmapBlobService
{
    public async Task<Result<UploadedBlob>> UploadAsync(string accountId, Stream content, string contentType, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountId);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(contentType);

        var connected = await connections.ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (!connected.IsSuccess)
        {
            return connected.Error;
        }

        var connection = connected.Value;
        var maxSize = connection.Session.Limits.MaxSizeUpload;
        if (maxSize > 0 && content.CanSeek && content.Length - content.Position > maxSize)
        {
            return JmapErrors.PayloadTooLarge($"The upload exceeds the mail server's limit of {maxSize} bytes.", "maxSizeUpload");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, connection.Session.UploadUrl(accountId))
        {
            Content = new StreamContent(content),
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        request.WithCredential(connection.Credential).MarkSafe(false);

        var sent = await JmapHttp.SendAsync(httpClientFactory.CreateClient(JmapHttpClients.Blob), request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (!sent.IsSuccess)
        {
            return sent.Error;
        }

        using var response = sent.Value;
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created))
        {
            return await FailAsync(connection, response, "upload", cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var uploaded = JsonSerializer.Deserialize(bytes, JmapJsonContext.Default.UploadResponse)
                ?? throw new JsonException("Empty upload response.");
            return new UploadedBlob(uploaded.AccountId, uploaded.BlobId, uploaded.Type, uploaded.Size);
        }
        catch (JsonException ex)
        {
            return JmapErrors.Unexpected($"The mail server's upload response is unreadable ({ex.Message}).");
        }
    }

    public async Task<Result<BlobContent>> DownloadAsync(string accountId, string blobId, string? name, string contentType, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountId);
        ArgumentException.ThrowIfNullOrEmpty(blobId);
        ArgumentException.ThrowIfNullOrEmpty(contentType);

        var connected = await connections.ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (!connected.IsSuccess)
        {
            return connected.Error;
        }

        var connection = connected.Value;
        var url = connection.Session.DownloadUrl(accountId, blobId, string.IsNullOrEmpty(name) ? "blob" : name, contentType);
        var first = await StartAsync(connection, url, cancellationToken).ConfigureAwait(false);
        if (!first.IsSuccess)
        {
            return first.Error;
        }

        var pending = first.Value;
        var headers = pending.Content.Headers;
        var opened = 0;

        async Task<Stream> OpenAsync(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref opened, 1) == 0)
            {
                return await ResponseStream.OpenAsync(pending, ct).ConfigureAwait(false);
            }

            var again = await StartAsync(connection, url, ct).ConfigureAwait(false);
            return again.IsSuccess
                ? await ResponseStream.OpenAsync(again.Value, ct).ConfigureAwait(false)
                : throw new IOException($"Blob download failed: {again.Error.Message}");
        }

        return new BlobContent(headers.ContentType?.ToString() ?? contentType, headers.ContentLength, OpenAsync)
        {
            FileName = name,
        };
    }

    private async Task<Result<HttpResponseMessage>> StartAsync(JmapConnection connection, Uri url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url).WithCredential(connection.Credential);
        var sent = await JmapHttp.SendAsync(httpClientFactory.CreateClient(JmapHttpClients.Blob), request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!sent.IsSuccess)
        {
            return sent.Error;
        }

        var response = sent.Value;
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            return await FailAsync(connection, response, "download", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Error> FailAsync(JmapConnection connection, HttpResponseMessage response, string context, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            connections.Reject(connection.Credential);
        }

        return await JmapHttp.ReadFailureAsync(response, time, context, connection.Credential.Strategy, cancellationToken).ConfigureAwait(false);
    }
}
