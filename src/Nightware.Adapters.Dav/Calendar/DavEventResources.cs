using Nightware.Adapters.Dav.Core;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Calendar;

/// <summary>A stored event resource as read for an edit: its id (resource or occurrence), body and ETag.</summary>
internal sealed record StoredEvent(DavItemId Id, string Body, string? ETag);

/// <summary>Loads and saves the resource behind an event id, after checking it belongs to the mailbox.</summary>
internal sealed class DavEventResources(IDavClient dav, DavCalendarScopes scopes)
{
    public const string ContentType = "text/calendar; charset=utf-8";

    public async Task<Result<StoredEvent>> LoadAsync(MailboxRef mailbox, EventId id, CancellationToken cancellationToken)
    {
        var parsed = await ResolveAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        if (!parsed.IsSuccess)
        {
            return parsed.Error;
        }

        var resource = await dav.GetAsync(parsed.Value.Href, cancellationToken).ConfigureAwait(false);
        return resource.IsSuccess ? new StoredEvent(parsed.Value, resource.Value.Body, resource.Value.ETag) : resource.Error;
    }

    /// <summary>The id, once it is known to name a resource in one of the mailbox's calendars.</summary>
    public async Task<Result<DavItemId>> ResolveAsync(MailboxRef mailbox, EventId id, CancellationToken cancellationToken)
    {
        var parsed = id.ToDavItemId();
        if (!parsed.IsSuccess)
        {
            return parsed.Error;
        }

        var scope = await scopes.ResolveAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!scope.IsSuccess)
        {
            return scope.Error;
        }

        return scope.Value.Contains(parsed.Value)
            ? parsed.Value
            : Error.NotFound("The specified object was not found in the store.", "id");
    }

    /// <summary>Writes an edited resource back, guarded by the ETag it was read with.</summary>
    public async Task<Result> SaveAsync(StoredEvent stored, string ics, CancellationToken cancellationToken)
    {
        var precondition = stored.ETag is { } etag ? DavPrecondition.Matching(etag) : DavPrecondition.None;
        var put = await dav.PutAsync(stored.Id.Href, ics, ContentType, precondition, cancellationToken).ConfigureAwait(false);
        return put.ToResult();
    }

    public Task<Result> DeleteAsync(StoredEvent stored, CancellationToken cancellationToken) =>
        dav.DeleteAsync(stored.Id.Href, stored.ETag is { } etag ? DavPrecondition.Matching(etag) : DavPrecondition.None, cancellationToken);
}
