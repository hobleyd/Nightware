using System.Buffers.Text;
using System.Text.Json;

using Nightware.Adapters.Dav.Calendar;
using Nightware.Adapters.Dav.Core;
using Nightware.Application.Contacts;
using Nightware.Domain.Contacts;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Contacts;

/// <summary>
/// <c>contactFolders/{id}/contacts/delta</c> over CardDAV <c>sync-collection</c> (ADR 0006 D7): the changed cards
/// are fetched by multiget and the removed ones reported <c>@removed</c>; the initial sync is every card. The
/// token names its folder, so it cannot be replayed against another one.
/// </summary>
internal sealed class DavContactDelta(DavContactStore contacts, IDavClient dav, DavPaths paths) : IContactDelta
{
    public async Task<Result<DeltaPage<Contact>>> DeltaAsync(MailboxRef mailbox, ContactFolderId folder, DeltaToken? token, CancellationToken cancellationToken)
    {
        var book = await contacts.BookAsync(mailbox, folder, cancellationToken).ConfigureAwait(false);
        if (!book.IsSuccess)
        {
            return book.Error;
        }

        // Without a token, an unlimited sync-collection lists every card with the token they are current as of
        // (Sabre refuses a size-limited initial sync with 507, so it is not paged).
        string? syncToken = null;
        if (token is { } t)
        {
            var cursor = Decode(t);
            if (cursor is null || cursor.Value.Folder != folder.Value)
            {
                return Error.SyncStateGone("The delta token is invalid or has expired; start again without one.");
            }

            syncToken = cursor.Value.SyncToken;
        }

        var bookHref = book.Value.Href;
        var sync = await dav.SyncCollectionAsync(bookHref, syncToken, [DavNames.GetETag], null, cancellationToken).ConfigureAwait(false);
        if (!sync.IsSuccess)
        {
            return sync.Error;
        }

        var entries = await UpsertsAsync(bookHref, sync.Value.Changed, cancellationToken).ConfigureAwait(false);
        if (!entries.IsSuccess)
        {
            return entries.Error;
        }

        entries.Value.AddRange(sync.Value.Removed.Select(paths.ToRelative).OfType<string>()
            .Select(static href => new DeltaEntry<Contact>.Removed(DavItemId.Resource(href).ToContactId().Value, RemovedReason.Deleted)));
        var next = Encode(sync.Value.SyncToken, folder);
        return sync.Value.IsTruncated ? DeltaPage<Contact>.More(entries.Value, next) : DeltaPage<Contact>.Done(entries.Value, next);
    }

    private async Task<Result<List<DeltaEntry<Contact>>>> UpsertsAsync(string bookHref, IReadOnlyList<DavResponse> changed, CancellationToken cancellationToken)
    {
        var bookPath = paths.ToRelative(bookHref);
        var hrefs = changed.Where(static r => r.IsFound)
            .Select(r => (Relative: paths.ToRelative(r.Href), Absolute: paths.ToAbsolutePath(r.Href)))
            .Where(h => h.Relative is not null && h.Absolute is not null && h.Relative != bookPath)
            .OrderBy(static h => h.Relative, StringComparer.Ordinal)
            .ToList();
        var fetched = await contacts.FetchAsync(bookHref, [.. hrefs.Select(static h => h.Absolute!)], cancellationToken).ConfigureAwait(false);
        return fetched.IsSuccess
            ? hrefs.Select(h => fetched.Value.GetValueOrDefault(h.Relative!)).OfType<Contact>().Select(static c => (DeltaEntry<Contact>)new DeltaEntry<Contact>.Upsert(c)).ToList()
            : fetched.Error;
    }

    private static DeltaToken Encode(string syncToken, ContactFolderId folder) =>
        new(Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new[] { syncToken, folder.Value }, DavCursorJsonContext.Default.StringArray)));

    private static (string SyncToken, string Folder)? Decode(DeltaToken token)
    {
        try
        {
            return JsonSerializer.Deserialize(Base64Url.DecodeFromChars(token.Value), DavCursorJsonContext.Default.StringArray) is [var sync, var folder]
                ? (sync, folder)
                : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }
}
