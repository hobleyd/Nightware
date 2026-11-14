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
/// books. Read-only — the contract has no contact writes, so they answer <see cref="ErrorKind.Unsupported"/>.
/// </summary>
/// <remarks>
/// <para>Nextcloud also mounts address books it generates into every user's home: the system address book
/// (<c>z-server-generated--system</c>, every account — the directory, already served by <c>/users</c>) and
/// app books such as Contacts Interaction's "Recently contacted" (<c>z-app-generated--…</c>). They are not
/// personal contacts and are skipped.</para>
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
        if (!books.IsSuccess)
        {
            return books.Error;
        }

        // Href: relative to the DAV root (the id form); Absolute: as Sabre returned it, which is what a
        // multiget must name (Sabre refuses hrefs outside its base URI).
        var cards = new List<(DavAddressBook Book, string Href, string Absolute)>();
        foreach (var book in books.Value)
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
            var fetched = await dav.ReportAsync(
                    group.Key.Href,
                    DavDepth.One,
                    DavRequestBodies.AddressBookMultiget(group.Select(static c => c.Absolute)),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!fetched.IsSuccess)
            {
                return fetched.Error;
            }

            foreach (var response in fetched.Value.Responses.Where(static r => r.IsFound && r.AddressData is not null))
            {
                if (paths.ToRelative(response.Href) is { } href && Parse(response.AddressData!, href) is { } contact)
                {
                    contacts[href] = contact;
                }
            }
        }

        IReadOnlyList<Contact> page = [.. window.Select(c => contacts.GetValueOrDefault(c.Href)).OfType<Contact>()];
        var next = offset + window.Count < cards.Count ? ContactPageToken.Encode(offset + window.Count) : null;
        return new Page<Contact>(page, next);
    }

    public async Task<Result<Contact>> GetAsync(MailboxRef mailbox, ContactId id, CancellationToken cancellationToken)
    {
        var item = id.ToDavItemId();
        if (!item.IsSuccess)
        {
            return item.Error;
        }

        var principal = await discovery.DiscoverAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!principal.IsSuccess)
        {
            return principal.Error;
        }

        // An id only proves its href is inside the DAV root; it must also be this mailbox's card.
        if (principal.Value.AddressBookHomeSet is not { } home
            || paths.ToRelative(home) is not { } homePath
            || !item.Value.IsWithin(homePath))
        {
            return NotFound();
        }

        var resource = await dav.GetAsync(item.Value.Href, cancellationToken).ConfigureAwait(false);
        if (!resource.IsSuccess)
        {
            return resource.Error;
        }

        var parsed = codec.Parse(resource.Value.Body, item.Value.Href);
        return parsed.IsSuccess ? parsed.Value.Contact : NotFound();
    }

    public Task<Result<Contact>> CreateAsync(MailboxRef mailbox, ContactDraft draft, CancellationToken cancellationToken) =>
        Task.FromResult<Result<Contact>>(ReadOnly());

    public Task<Result<Contact>> UpdateAsync(MailboxRef mailbox, ContactId id, ContactDraft patch, CancellationToken cancellationToken) =>
        Task.FromResult<Result<Contact>>(ReadOnly());

    public Task<Result> DeleteAsync(MailboxRef mailbox, ContactId id, CancellationToken cancellationToken) =>
        Task.FromResult<Result>(ReadOnly());

    internal static bool IsGenerated(DavAddressBook book)
    {
        var segment = book.Href.TrimEnd('/');
        segment = segment[(segment.LastIndexOf('/') + 1)..];
        return GeneratedBookPrefixes.Any(p => segment.StartsWith(p, StringComparison.Ordinal));
    }

    private async Task<Result<IReadOnlyList<DavAddressBook>>> AddressBooksAsync(MailboxRef mailbox, CancellationToken cancellationToken)
    {
        var principal = await discovery.DiscoverAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!principal.IsSuccess)
        {
            return principal.Error;
        }

        var books = await catalog.ListAddressBooksAsync(principal.Value, cancellationToken).ConfigureAwait(false);
        return books.IsSuccess
            ? Result<IReadOnlyList<DavAddressBook>>.Success([.. books.Value.Where(static b => !IsGenerated(b)).OrderBy(static b => b.Href, StringComparer.Ordinal)])
            : books.Error;
    }

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

    private static Error ReadOnly() => Error.Unsupported("Contacts are read-only.");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping contact {Href}: {Reason}")]
    private static partial void LogUnparsable(ILogger logger, string href, string reason);
}
