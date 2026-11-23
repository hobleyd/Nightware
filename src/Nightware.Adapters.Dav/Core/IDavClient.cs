using System.Text.Json;
using System.Xml.Linq;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Core;

/// <summary>
/// Low-level WebDAV/CalDAV/CardDAV/OCS client acting as the current caller. Every
/// <c>href</c> may be server-absolute (as Sabre returns it), a full URL on the
/// Nextcloud origin, or relative to the DAV root; anything resolving outside the
/// DAV root is refused with <see cref="ErrorKind.Validation"/> without a request.
/// All failures come back as <see cref="Error"/>s; caller cancellation still throws.
/// </summary>
public interface IDavClient
{
    Task<Result<DavMultistatus>> PropFindAsync(string href, DavDepth depth, IEnumerable<XName> properties, CancellationToken cancellationToken);

    /// <summary>Any REPORT answered with a multistatus (calendar-query, multiget, addressbook-query, principal-property-search…).</summary>
    Task<Result<DavMultistatus>> ReportAsync(string href, DavDepth depth, XDocument report, CancellationToken cancellationToken);

    /// <summary>RFC 6578 sync-collection. An invalid token yields <see cref="ErrorKind.SyncStateGone"/>.</summary>
    Task<Result<DavSyncResult>> SyncCollectionAsync(string collectionHref, string? syncToken, IEnumerable<XName> properties, int? limit, CancellationToken cancellationToken);

    /// <summary>CalDAV free-busy-query on one calendar; returns the VFREEBUSY iCalendar text.</summary>
    Task<Result<string>> FreeBusyQueryAsync(string calendarHref, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken);

    Task<Result<DavResource>> GetAsync(string href, CancellationToken cancellationToken);

    /// <summary>Streams a resource (files, attachments). Dispose the result.</summary>
    Task<Result<DavDownload>> OpenReadAsync(string href, CancellationToken cancellationToken);

    Task<Result<DavWriteResult>> PutAsync(string href, string body, string contentType, DavPrecondition precondition, CancellationToken cancellationToken);

    Task<Result<DavWriteResult>> PutAsync(string href, Stream content, string contentType, DavPrecondition precondition, CancellationToken cancellationToken);

    Task<Result> DeleteAsync(string href, DavPrecondition precondition, CancellationToken cancellationToken);

    /// <summary>Plain MKCOL, or RFC 5689 extended MKCOL when <paramref name="body"/> is given (address books).</summary>
    Task<Result> MkColAsync(string href, XDocument? body, CancellationToken cancellationToken);

    /// <summary>A plain <c>POST</c> of an XML body (e.g. Nextcloud's <c>oc:share</c> on a calendar).</summary>
    Task<Result> PostXmlAsync(string href, XDocument body, CancellationToken cancellationToken);

    Task<Result> MkCalendarAsync(string href, string? displayName, IEnumerable<string> components, string? color, CancellationToken cancellationToken);

    Task<Result> MoveAsync(string sourceHref, string destinationHref, bool overwrite, CancellationToken cancellationToken);

    Task<Result<DavMultistatus>> PropPatchAsync(string href, IEnumerable<XElement> set, IEnumerable<XName> remove, CancellationToken cancellationToken);

    /// <summary>RFC 6638 POST to a schedule outbox (free-busy VFREEBUSY request); one item per recipient.</summary>
    Task<Result<IReadOnlyList<DavScheduleResponseItem>>> PostToOutboxAsync(string outboxHref, string ics, CancellationToken cancellationToken);

    /// <summary>
    /// WebDAV <c>COPY</c> of a resource (a collection with everything below it, <c>Depth: infinity</c>). Without
    /// <paramref name="overwrite"/>, an existing destination is <see cref="ErrorKind.Conflict"/>.
    /// </summary>
    Task<Result> CopyAsync(string sourceHref, string destinationHref, bool overwrite, CancellationToken cancellationToken);

    /// <summary>An RFC 5323 <c>SEARCH</c> on the DAV root (Nextcloud's file search); the body names its own scope.</summary>
    Task<Result<DavMultistatus>> SearchAsync(XDocument body, CancellationToken cancellationToken);

    /// <summary>
    /// Sends <paramref name="method"/> (POST, PUT, DELETE) to an OCS endpoint with a form body; returns <c>ocs.data</c>.
    /// An OCS failure status maps as <see cref="OcsGetAsync"/>'s does.
    /// </summary>
    Task<Result<JsonElement>> OcsSendAsync(HttpMethod method, string path, IReadOnlyDictionary<string, string>? form, CancellationToken cancellationToken);

    /// <summary>GET an OCS endpoint (path below the OCS root, e.g. <c>apps/files_sharing/api/v1/shares</c>); returns <c>ocs.data</c>.</summary>
    Task<Result<JsonElement>> OcsGetAsync(string path, IReadOnlyDictionary<string, string>? query, CancellationToken cancellationToken);

    /// <summary>
    /// Any OCS request (ADR 0009 D8): JSON or form body, extra headers; returns <c>ocs.data</c> with the HTTP status
    /// and response headers (Talk's <c>X-Chat-Last-Given</c>, its session's <c>Set-Cookie</c>). A failure maps as
    /// <see cref="OcsGetAsync"/>'s does, and its message carries the reason the server put in <c>ocs.data</c>.
    /// </summary>
    Task<Result<OcsResponse>> OcsExchangeAsync(OcsRequest request, CancellationToken cancellationToken);
}
