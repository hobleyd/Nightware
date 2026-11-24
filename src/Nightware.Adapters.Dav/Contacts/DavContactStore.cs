using Microsoft.Extensions.Logging;

using Nightware.Adapters.Dav.Contacts.VCard;
using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Collections;
using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Application.Common.Query;
using Nightware.Application.Contacts;
using Nightware.Domain.Contacts;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Contacts;

/// <summary>
/// <see cref="IContactStore"/> over CardDAV: the mailbox owner's own and shared-with-them Nextcloud address
/// books. A contact's folder is its address book; new contacts go to the default book (<c>contacts</c>)
/// unless a folder is named. Writes are conditional on the card's ETag and keep what Graph does not map.
/// </summary>
/// <remarks>
/// <para>Nextcloud also mounts address books it generates into every user's home: the system address book
/// (<c>z-server-generated--system</c>, every account — the directory, already served by <c>/users</c>) and
/// app books such as Contacts Interaction's "Recently contacted" (<c>z-app-generated--…</c>). They are not
/// personal contacts and are skipped.</para>
/// <para>Another mailbox's contacts (<c>/users/{x}</c>: a shared mailbox, or a colleague) are the address books
/// <c>x</c> shared with the caller, which Nextcloud mounts in the caller's home with <c>owner-principal</c> = <c>x</c>;
/// <c>x</c>'s own home is not readable by others. None is 403, as for calendars (phase 27).</para>
/// <para>Paging is stateless: each page lists every card's href (an <c>addressbook-query</c> without data per
/// book), orders them by (book, card) href, slices at the offset in the <c>$skiptoken</c> and fetches only
/// that slice with <c>addressbook-multiget</c>. A card that cannot be parsed or vanished in between is left
/// out rather than failing the client's whole cache refresh.</para>
/// </remarks>
internal sealed partial class DavContactStore(
    IDavPrincipalDiscovery discovery,
    IDavCollectionCatalog catalog,
    IDavClient dav,
    DavPaths paths,
    VCardCodec codec,
    ILogger<DavContactStore> logger) : IContactStore
{
    /// <summary>Graph's default <c>/contacts</c> page size.</summary>
    public const int DefaultPageSize = 10;

    public const string DefaultBookName = "contacts";

    private const string VCardContentType = "text/vcard; charset=utf-8";

    private static readonly string[] GeneratedBookPrefixes = ["z-server-generated--", "z-app-generated--"];

    public async Task<Result<Page<Contact>>> ListAsync(MailboxRef mailbox, ItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (Unsupported(query) is { } option)
        {
            return Error.Validation($"{option} is not supported on contacts.", option);
        }

        var offset = 0;
        if (query.SkipToken is { } token && !ContactPageToken.TryDecode(token, out offset))
        {
            return Error.Validation("The $skiptoken is not valid.", "$skiptoken");
        }

        var books = await AddressBooksAsync(mailbox, cancellationToken).ConfigureAwait(false);
        return books.IsSuccess ? await PageAsync(books.Value, offset, query, cancellationToken).ConfigureAwait(false) : books.Error;
    }

    public async Task<Result<Page<Contact>>> ListInAsync(MailboxRef mailbox, ContactFolderId folder, ItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (Unsupported(query) is { } option)
        {
            return Error.Validation($"{option} is not supported on contacts.", option);
        }

        var offset = 0;
        if (query.SkipToken is { } token && !ContactPageToken.TryDecode(token, out offset))
        {
            return Error.Validation("The $skiptoken is not valid.", "$skiptoken");
        }

        var book = await BookAsync(mailbox, folder, cancellationToken).ConfigureAwait(false);
        return book.IsSuccess ? await PageAsync([book.Value], offset, query, cancellationToken).ConfigureAwait(false) : book.Error;
    }

    private async Task<Result<Page<Contact>>> PageAsync(IReadOnlyList<DavAddressBook> books, int offset, ItemQuery query, CancellationToken cancellationToken)
    {
        // Href: relative to the DAV root (the id form); Absolute: as Sabre returned it, which is what a
        // multiget must name (Sabre refuses hrefs outside its base URI).
        var cards = new List<(DavAddressBook Book, string Href, string Absolute)>();
        foreach (var book in books)
        {
            var members = await dav.ReportAsync(book.Href, DavDepth.One, DavRequestBodies.AddressBookQuery(includeData: false), cancellationToken).ConfigureAwait(false);
            if (!members.IsSuccess)
            {
                return members.Error;
            }

            var bookPath = paths.ToRelative(book.Href);
            cards.AddRange(members.Value.Responses
                .Where(static r => r.IsFound)
                .Select(r => (Relative: paths.ToRelative(r.Href), Absolute: paths.ToAbsolutePath(r.Href)))
                .Where(h => h.Relative is not null && h.Absolute is not null && h.Relative != bookPath)
                .OrderBy(static h => h.Relative, StringComparer.Ordinal)
                .Select(h => (book, h.Relative!, h.Absolute!)));
        }

        var top = query.Top ?? DefaultPageSize;
        var window = cards.Skip(offset).Take(top).ToList();
        var contacts = new Dictionary<string, Contact>(StringComparer.Ordinal);
        foreach (var group in window.GroupBy(static c => c.Book))
        {
            var fetched = await FetchAsync(group.Key.Href, [.. group.Select(static c => c.Absolute)], cancellationToken).ConfigureAwait(false);
            if (!fetched.IsSuccess)
            {
                return fetched.Error;
            }

            foreach (var (href, contact) in fetched.Value)
            {
                contacts[href] = contact;
            }
        }

        IReadOnlyList<Contact> page = [.. window.Select(c => contacts.GetValueOrDefault(c.Href)).OfType<Contact>()];
        var next = offset + window.Count < cards.Count ? ContactPageToken.Encode(offset + window.Count) : null;
        return new Page<Contact>(page, next) { Total = query.Count ? cards.Count : null };
    }

    public async Task<Result<Contact>> GetAsync(MailboxRef mailbox, ContactId id, CancellationToken cancellationToken)
    {
        var item = await ResolveAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        return item.IsSuccess ? await ReadAsync(item.Value, cancellationToken).ConfigureAwait(false) : item.Error;
    }

    public async Task<Result<Contact>> CreateAsync(MailboxRef mailbox, ContactFolderId? folder, ContactDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var book = await BookAsync(mailbox, folder, cancellationToken).ConfigureAwait(false);
        if (!book.IsSuccess)
        {
            return book.Error;
        }

        if (paths.ToRelative(book.Value.Href) is not { } bookPath)
        {
            return Error.NotFound("The specified folder was not found in the store.", "id");
        }

        var uid = Guid.NewGuid().ToString();
        var href = bookPath + uid + ".vcf";
        var written = await dav.PutAsync(href, codec.Create(draft, uid), VCardContentType, DavPrecondition.CreateOnly, cancellationToken).ConfigureAwait(false);
        return written.IsSuccess ? await ReadAsync(DavItemId.Resource(href), cancellationToken).ConfigureAwait(false) : written.Error;
    }

    public async Task<Result<Contact>> UpdateAsync(MailboxRef mailbox, ContactId id, ContactDraft patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var edited = await EditAsync(mailbox, id, vcf => codec.Apply(vcf, patch), cancellationToken).ConfigureAwait(false);
        return edited.IsSuccess ? await ReadAsync(edited.Value, cancellationToken).ConfigureAwait(false) : edited.Error;
    }

    public async Task<Result> DeleteAsync(MailboxRef mailbox, ContactId id, CancellationToken cancellationToken)
    {
        var item = await ResolveAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        return item.IsSuccess ? await dav.DeleteAsync(item.Value.Href, DavPrecondition.None, cancellationToken).ConfigureAwait(false) : item.Error;
    }

    public async Task<Result<BlobContent>> GetPhotoAsync(MailboxRef mailbox, ContactId id, CancellationToken cancellationToken)
    {
        var item = await ResolveAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        if (!item.IsSuccess)
        {
            return item.Error;
        }

        var resource = await dav.GetAsync(item.Value.Href, cancellationToken).ConfigureAwait(false);
        if (!resource.IsSuccess)
        {
            return resource.Error;
        }

        var photo = codec.ReadPhoto(resource.Value.Body);
        if (!photo.IsSuccess)
        {
            return NotFound();
        }

        if (photo.Value is not { } found)
        {
            return Error.NotFound("The contact has no photo.");
        }

        return new BlobContent(found.ContentType, found.Bytes.Length, _ => Task.FromResult<Stream>(new MemoryStream(found.Bytes, writable: false)));
    }

    public async Task<Result> SetPhotoAsync(MailboxRef mailbox, ContactId id, byte[] photo, string contentType, CancellationToken cancellationToken)
    {
        var edited = await EditAsync(mailbox, id, vcf => codec.WritePhoto(vcf, photo, contentType), cancellationToken).ConfigureAwait(false);
        return edited.IsSuccess ? Result.Success() : edited.Error;
    }

    /// <summary>
    /// The cards at <paramref name="absoluteHrefs"/> (server-absolute, as a multiget must name them) by one
    /// <c>addressbook-multiget</c>, keyed by DAV-relative href. Unparsable or vanished cards are left out.
    /// </summary>
    internal async Task<Result<Dictionary<string, Contact>>> FetchAsync(string bookHref, IReadOnlyCollection<string> absoluteHrefs, CancellationToken cancellationToken)
    {
        var contacts = new Dictionary<string, Contact>(StringComparer.Ordinal);
        if (absoluteHrefs.Count == 0)
        {
            return contacts;
        }

        var fetched = await dav.ReportAsync(bookHref, DavDepth.One, DavRequestBodies.AddressBookMultiget(absoluteHrefs), cancellationToken).ConfigureAwait(false);
        if (!fetched.IsSuccess)
        {
            return fetched.Error;
        }

        foreach (var response in fetched.Value.Responses.Where(static r => r.IsFound && r.AddressData is not null))
        {
            if (paths.ToRelative(response.Href) is { } href && Parse(response.AddressData!, href) is { } contact)
            {
                contacts[href] = contact with { ParentFolderId = FolderOf(href), ChangeKey = response.ETag };
            }
        }

        return contacts;
    }

    /// <summary>The folder id of an address book (or of the book a card href is in).</summary>
    internal static ContactFolderId FolderOf(string relativeHref)
    {
        var book = relativeHref.EndsWith('/') ? relativeHref : relativeHref[..(relativeHref.LastIndexOf('/') + 1)];
        return new ContactFolderId(DavItemId.Resource(book).ToString());
    }

    /// <summary>The card behind <paramref name="id"/>, provided it is in this mailbox's address-book home.</summary>
    private async Task<Result<DavItemId>> ResolveAsync(MailboxRef mailbox, ContactId id, CancellationToken cancellationToken)
    {
        var item = id.ToDavItemId();
        if (!item.IsSuccess)
        {
            return NotFound();
        }

        var scope = await ScopeAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!scope.IsSuccess)
        {
            return scope.Error;
        }

        // An id only proves its href is inside the DAV root; it must also be this mailbox's card: in its home, or (for
        // another mailbox) in one of the books it shared with the caller.
        var within = scope.Value.Own
            ? scope.Value.Owner.AddressBookHomeSet is { } home && paths.ToRelative(home) is { } homePath && item.Value.IsWithin(homePath)
            : scope.Value.Books.Any(b => paths.ToRelative(b.Href) is { } bookPath && item.Value.IsWithin(bookPath));
        return within ? item.Value : NotFound();
    }

    private async Task<Result<Contact>> ReadAsync(DavItemId item, CancellationToken cancellationToken)
    {
        var resource = await dav.GetAsync(item.Href, cancellationToken).ConfigureAwait(false);
        if (!resource.IsSuccess)
        {
            return resource.Error;
        }

        var parsed = codec.Parse(resource.Value.Body, item.Href);
        return parsed.IsSuccess
            ? parsed.Value.Contact with { ParentFolderId = FolderOf(item.Href), ChangeKey = resource.Value.ETag }
            : NotFound();
    }

    /// <summary>GET, edit, PUT back on the same ETag (a concurrent change fails rather than being lost).</summary>
    private async Task<Result<DavItemId>> EditAsync(MailboxRef mailbox, ContactId id, Func<string, Result<string>> edit, CancellationToken cancellationToken)
    {
        var item = await ResolveAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        if (!item.IsSuccess)
        {
            return item.Error;
        }

        var resource = await dav.GetAsync(item.Value.Href, cancellationToken).ConfigureAwait(false);
        if (!resource.IsSuccess)
        {
            return resource.Error;
        }

        var edited = edit(resource.Value.Body);
        if (!edited.IsSuccess)
        {
            return edited.Error;
        }

        var precondition = resource.Value.ETag is { } etag ? DavPrecondition.Matching(etag) : DavPrecondition.None;
        var written = await dav.PutAsync(item.Value.Href, edited.Value, VCardContentType, precondition, cancellationToken).ConfigureAwait(false);
        return written.IsSuccess ? item.Value : written.Error;
    }

    /// <summary>The address book to create in: the named folder, or the default book.</summary>
    internal async Task<Result<DavAddressBook>> BookAsync(MailboxRef mailbox, ContactFolderId? folder, CancellationToken cancellationToken)
    {
        var books = await AddressBooksAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!books.IsSuccess)
        {
            return books.Error;
        }

        var match = folder is { } wanted
            ? books.Value.FirstOrDefault(b => paths.ToRelative(b.Href) is { } path && FolderOf(path) == wanted)
            : DefaultBook(books.Value) ?? books.Value.FirstOrDefault(static b => b.IsShared && !b.IsReadOnly);
        return match is null ? Error.NotFound("The specified folder was not found in the store.", "id") : match;
    }

    /// <summary>Nextcloud's default book is <c>contacts</c>; failing that, the first of the user's own.</summary>
    internal static DavAddressBook? DefaultBook(IReadOnlyList<DavAddressBook> books) =>
        books.FirstOrDefault(b => b.Href.TrimEnd('/').EndsWith("/" + DefaultBookName, StringComparison.Ordinal))
        ?? books.FirstOrDefault(b => !b.IsShared);

    internal static bool IsGenerated(DavAddressBook book)
    {
        var segment = book.Href.TrimEnd('/');
        segment = segment[(segment.LastIndexOf('/') + 1)..];
        return GeneratedBookPrefixes.Any(p => segment.StartsWith(p, StringComparison.Ordinal));
    }

    internal async Task<Result<IReadOnlyList<DavAddressBook>>> AddressBooksAsync(MailboxRef mailbox, CancellationToken cancellationToken)
    {
        var scope = await ScopeAsync(mailbox, cancellationToken).ConfigureAwait(false);
        return scope.IsSuccess ? Result<IReadOnlyList<DavAddressBook>>.Success(scope.Value.Books) : scope.Error;
    }

    /// <summary>
    /// The mailbox's address books as the caller sees them: its own home's (its own and those shared with it), or, for
    /// another mailbox, the books that mailbox shared with the caller.
    /// </summary>
    internal async Task<Result<(DavPrincipal Owner, bool Own, IReadOnlyList<DavAddressBook> Books)>> ScopeAsync(MailboxRef mailbox, CancellationToken cancellationToken)
    {
        var principal = await discovery.DiscoverAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!principal.IsSuccess)
        {
            return principal.Error;
        }

        var reader = principal.Value;
        if (!principal.Value.IsCurrentUser)
        {
            var self = await discovery.CurrentUserAsync(cancellationToken).ConfigureAwait(false);
            if (!self.IsSuccess)
            {
                return self.Error;
            }

            reader = self.Value;
        }

        var books = await catalog.ListAddressBooksAsync(reader, cancellationToken).ConfigureAwait(false);
        if (!books.IsSuccess)
        {
            return books.Error;
        }

        var listed = books.Value.Where(b => !IsGenerated(b) && (principal.Value.IsCurrentUser || (b.IsShared && IsOwnedBy(b, principal.Value))))
            .OrderBy(static b => b.Href, StringComparer.Ordinal)
            .ToList();
        return !principal.Value.IsCurrentUser && listed.Count == 0
            ? NoBooks(mailbox)
            : (principal.Value, principal.Value.IsCurrentUser, listed);
    }

    /// <summary>Nextcloud reports <c>owner-principal</c> relative to the DAV root (<c>principals/users/bob</c>).</summary>
    private static bool IsOwnedBy(DavAddressBook book, DavPrincipal owner) =>
        book.OwnerPrincipal is { Length: > 0 } principal
        && owner.Href.TrimEnd('/').EndsWith("/" + principal.Trim('/'), StringComparison.Ordinal);

    private static Error NoBooks(MailboxRef mailbox) =>
        Error.Forbidden($"No address book of {mailbox.Address} is available to you.");

    private Contact? Parse(string vcf, string href)
    {
        var parsed = codec.Parse(vcf, href);
        if (parsed.IsSuccess)
        {
            return parsed.Value.Contact;
        }

        LogUnparsable(logger, href, parsed.Error.Message);
        return null;
    }

    private static string? Unsupported(ItemQuery query) =>
        query.Filter is not null ? "$filter"
        : query.Search is not null ? "$search"
        : query.OrderBy.Count > 0 ? "$orderby"
        : query.Skip is not null ? "$skip"
        : query.Expand.Count > 0 ? "$expand"
        : null;

    private static Error NotFound() => Error.NotFound("The specified object was not found in the store.", "id");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping contact {Href}: {Reason}")]
    private static partial void LogUnparsable(ILogger logger, string href, string reason);
}
