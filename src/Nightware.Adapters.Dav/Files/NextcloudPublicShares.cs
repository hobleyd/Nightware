using System.Net;
using System.Net.Http.Headers;
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Polly;

using Nightware.Adapters.Dav.Core;
using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Files;

/// <summary>
/// Public shares (<c>/s/{token}</c>) through Nextcloud's public-share WebDAV,
/// <c>{BaseUrl}public.php/dav/files/{token}</c>, which serves a file share's single file at its root.
/// </summary>
/// <remarks>
/// <para>Requests carry <b>no credential</b>: a public share is readable by anyone holding the link, and the
/// caller's token belongs to their own account, not to the share. This is also outside the DAV root that
/// <see cref="IDavClient"/> is confined to, hence a typed client of its own (cookie-free: Nextcloud starts a
/// session for every public-share request).</para>
/// <para>Nextcloud's answers, recorded from 35: an unknown token is <b>503</b> carrying
/// <c>Sabre\DAV\Exception\NotFound</c> (mapped to 404 — a 503 would make the client retry five times); a
/// password-protected share is <b>401</b> (mapped to 403 — the client reads 401 as "sign in again"). A folder
/// share is answered, and comes back as a folder the client will not preview.</para>
/// </remarks>
internal sealed partial class NextcloudPublicShares(
    HttpClient http,
    IOptions<DavOptions> options,
    TimeProvider timeProvider,
    ILogger<NextcloudPublicShares> logger)
{
    private static readonly HttpMethod PropFind = new("PROPFIND");

    public async Task<Result<SharedItem>> GetItemAsync(string token, ShareLink link, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(PropFind, ShareUri(token))
        {
            Content = new StringContent(SharedItemMapping.PropFindBody(), Encoding.UTF8, "application/xml"),
        };
        request.Headers.Add("Depth", "0");
        var sent = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!sent.IsSuccess)
        {
            return sent.Error;
        }

        using var response = sent.Value;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.MultiStatus)
        {
            return Fail(response, body);
        }

        DavMultistatus multistatus;
        try
        {
            multistatus = DavXml.ParseMultistatus(body);
        }
        catch (System.Xml.XmlException)
        {
            return Error.BackendUnavailable("The groupware server sent an unreadable share listing.");
        }

        return multistatus.Responses.FirstOrDefault(static r => r.IsFound) is { } item
            ? SharedItemMapping.ToItem(item, link, fallbackId: token)
            : Error.NotFound("The share does not exist.");
    }

    public async Task<Result<BlobContent>> GetContentAsync(string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ShareUri(token));
        var sent = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!sent.IsSuccess)
        {
            return sent.Error;
        }

        var response = sent.Value;
        if (response.StatusCode != HttpStatusCode.OK)
        {
            using (response)
            {
                return Fail(response, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            }
        }

        var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        Stream body = new ResponseStream(content, response);
        return new BlobContent(response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream", response.Content.Headers.ContentLength, _ => Task.FromResult(body));
    }

    internal Uri ShareUri(string token)
    {
        var baseUrl = options.Value.BaseUrl ?? throw new InvalidOperationException("Dav:BaseUrl is not configured.");
        return new Uri(baseUrl, $"{baseUrl.AbsolutePath.TrimEnd('/')}/public.php/dav/files/{Uri.EscapeDataString(token)}");
    }

    private async Task<Result<HttpResponseMessage>> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            LogUnreachable(logger, ex.Message);
            return Error.BackendUnavailable("The groupware server is unreachable.");
        }
        catch (Exception ex) when (ex is TimeoutException or ExecutionRejectedException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            LogUnreachable(logger, "timed out");
            return Error.BackendUnavailable("The groupware server timed out.");
        }
    }

    private Error Fail(HttpResponseMessage response, string body)
    {
        var status = (int)response.StatusCode;
        var error = status switch
        {
            401 => Error.Forbidden("The share is protected by a password; open it in the browser."),
            503 when body.Contains(@"Sabre\DAV\Exception\NotFound", StringComparison.Ordinal) => Error.NotFound("The share does not exist."),
            _ => DavErrors.Map(response.StatusCode, body, DavErrors.RetryAfterSeconds(response, timeProvider)),
        };
        LogFailed(logger, status, error.Kind);
        return error;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Nextcloud public share request failed: HTTP {Status} → {Kind}")]
    private static partial void LogFailed(ILogger logger, int status, ErrorKind kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Nextcloud public share endpoint unreachable: {Reason}")]
    private static partial void LogUnreachable(ILogger logger, string reason);
}
