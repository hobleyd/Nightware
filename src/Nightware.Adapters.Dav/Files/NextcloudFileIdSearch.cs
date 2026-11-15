using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

using Microsoft.Extensions.Logging;

using Polly;

using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Adapters.Dav.Core.Http;
using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Files;

/// <summary>
/// Finds a file by its Nextcloud file id (the <c>/f/{fileId}</c> internal link) in the caller's own Files,
/// with a WebDAV <c>SEARCH</c> (RFC 5323 basicsearch on <c>oc:fileid</c>) scoped to <c>/files/{uid}</c>.
/// </summary>
/// <remarks>
/// The search runs as the caller, so it finds the file only where the caller can already see it — their own
/// file, or one shared with them (Nextcloud mounts shares in the recipient's home and keeps the file id).
/// No match is <see cref="ErrorKind.NotFound"/> either way, as Nextcloud itself does not say which.
/// <see cref="IDavClient"/> has no SEARCH, hence this typed client on the same pipeline (resilience, then the
/// caller's credential; no cookies, no redirects).
/// </remarks>
internal sealed partial class NextcloudFileIdSearch(
    HttpClient http,
    DavPaths paths,
    IUserContext user,
    IDavPrincipalDiscovery discovery,
    IBackendCredentials credentials,
    TimeProvider timeProvider,
    ILogger<NextcloudFileIdSearch> logger)
{
    private static readonly HttpMethod Search = new("SEARCH");

    /// <summary>The file's multistatus entry (href and <see cref="SharedItemMapping.Properties"/>).</summary>
    public async Task<Result<DavResponse>> FindAsync(long fileId, CancellationToken cancellationToken)
    {
        var principal = await discovery.DiscoverAsync(new MailboxRef(user.Principal.Mail), cancellationToken).ConfigureAwait(false);
        if (!principal.IsSuccess)
        {
            return principal.Error;
        }

        if (paths.ToRelative(principal.Value.Href)?.TrimEnd('/').Split('/') is not ["principals", "users", { Length: > 0 } uid])
        {
            return Error.NotFound("Only user accounts have Files.");
        }

        var credential = await credentials.ForAsync(Backend.Groupware, cancellationToken).ConfigureAwait(false);
        if (!credential.IsSuccess)
        {
            return credential.Error;
        }

        var allowed = DavCredentialPolicy.Check(credential.Value);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        using var request = new HttpRequestMessage(Search, paths.RootUri)
        {
            Content = new StringContent(Body(uid, fileId), Encoding.UTF8, "text/xml"),
        };
        request.Options.Set(DavRequestOptions.Credential, allowed.Value);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.MultiStatus)
            {
                var error = DavErrors.Map(response.StatusCode, body, DavErrors.RetryAfterSeconds(response, timeProvider), credential: allowed.Value.Strategy);
                LogFailed(logger, (int)response.StatusCode, error.Kind);
                return error;
            }

            return DavXml.ParseMultistatus(body).Responses.FirstOrDefault(static r => r.IsFound) is { } match
                ? match
                : Error.NotFound("No file with that id is shared with you.");
        }
        catch (System.Xml.XmlException)
        {
            return Error.BackendUnavailable("The groupware server sent an unreadable search result.");
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

    /// <param name="uid">The uid segment exactly as the principal href carries it.</param>
    internal static string Body(string uid, long fileId) =>
        new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(DavNs.Dav + "searchrequest",
                new XAttribute(XNamespace.Xmlns + "d", DavNs.Dav),
                new XAttribute(XNamespace.Xmlns + "oc", DavNs.OwnCloud),
                new XElement(DavNs.Dav + "basicsearch",
                    new XElement(DavNs.Dav + "select", new XElement(DavNames.Prop, SharedItemMapping.Properties.Select(static p => new XElement(p)))),
                    new XElement(DavNs.Dav + "from",
                        new XElement(DavNs.Dav + "scope",
                            new XElement(DavNames.Href, $"/files/{uid}"),
                            new XElement(DavNs.Dav + "depth", "infinity"))),
                    new XElement(DavNs.Dav + "where",
                        new XElement(DavNs.Dav + "eq",
                            new XElement(DavNames.Prop, new XElement(SharedItemMapping.FileId)),
                            new XElement(DavNs.Dav + "literal", fileId.ToString(CultureInfo.InvariantCulture)))),
                    new XElement(DavNs.Dav + "orderby")))).ToString(SaveOptions.DisableFormatting);

    [LoggerMessage(Level = LogLevel.Information, Message = "Nextcloud file-id search failed: HTTP {Status} → {Kind}")]
    private static partial void LogFailed(ILogger logger, int status, ErrorKind kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Nextcloud DAV unreachable for a file-id search: {Reason}")]
    private static partial void LogUnreachable(ILogger logger, string reason);
}
