using System.Net.Http.Headers;

namespace Nightware.Adapters.Dav.Core;

/// <summary>ETag concurrency for writes.</summary>
public readonly record struct DavPrecondition
{
    private DavPrecondition(string? ifMatch, bool ifNoneMatchAny)
    {
        IfMatch = ifMatch;
        IfNoneMatchAny = ifNoneMatchAny;
    }

    /// <summary>Unconditional write.</summary>
    public static DavPrecondition None => default;

    /// <summary><c>If-None-Match: *</c> — create only; an existing resource yields <c>Conflict</c>.</summary>
    public static DavPrecondition CreateOnly => new(null, true);

    public string? IfMatch { get; }

    public bool IfNoneMatchAny { get; }

    /// <summary><c>If-Match: etag</c> — update only if unchanged; a stale ETag yields <c>PreconditionFailed</c>.</summary>
    public static DavPrecondition Matching(string etag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(etag);
        return new DavPrecondition(etag, false);
    }

    internal DavRequestIntent Intent => IfNoneMatchAny
        ? DavRequestIntent.CreateOnly
        : IfMatch is null ? DavRequestIntent.Default : DavRequestIntent.Conditional;

    internal void Apply(HttpRequestMessage request)
    {
        if (IfNoneMatchAny)
        {
            request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        }
        else if (IfMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", IfMatch);
        }
    }
}

/// <summary>A resource body read with GET.</summary>
/// <param name="Href">Server-absolute path that was requested.</param>
public sealed record DavResource(string Href, string Body, string? ETag, string? ContentType);

/// <summary>The outcome of a PUT.</summary>
/// <param name="ETag">
/// Null when the server did not return one — Sabre omits it whenever it altered the stored
/// object (e.g. the scheduling plugin added SCHEDULE-STATUS). Re-GET when you need it.
/// </param>
public sealed record DavWriteResult(string Href, string? ETag, bool Created);

/// <summary>A streamed GET; dispose it to release the connection.</summary>
public sealed class DavDownload : IDisposable
{
    private readonly HttpResponseMessage _response;

    internal DavDownload(HttpResponseMessage response, Stream content)
    {
        _response = response;
        Content = content;
    }

    public Stream Content { get; }

    public string ContentType => _response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";

    public long? Length => _response.Content.Headers.ContentLength;

    public string? ETag => _response.Headers.ETag?.ToString();

    public void Dispose()
    {
        Content.Dispose();
        _response.Dispose();
    }
}

/// <summary>An RFC 6578 sync-collection round.</summary>
/// <param name="Changed">Members created or modified since the token (with the requested properties).</param>
/// <param name="Removed">Hrefs of members deleted since the token.</param>
/// <param name="SyncToken">The token to send next time.</param>
/// <param name="IsTruncated">The server stopped early (507 on the collection); call again with <paramref name="SyncToken"/>.</param>
public sealed record DavSyncResult(IReadOnlyList<DavResponse> Changed, IReadOnlyList<string> Removed, string SyncToken, bool IsTruncated);

/// <summary>One recipient's answer in a CalDAV scheduling response (RFC 6638 §3.2.2).</summary>
/// <param name="Recipient">The <c>mailto:</c> URI.</param>
/// <param name="RequestStatus">e.g. <c>2.0;Success</c>, <c>3.7;Invalid calendar user</c>.</param>
/// <param name="CalendarData">The VFREEBUSY reply, when successful.</param>
public sealed record DavScheduleResponseItem(string Recipient, string RequestStatus, string? CalendarData)
{
    public bool IsSuccess => RequestStatus.StartsWith("2.", StringComparison.Ordinal);
}
