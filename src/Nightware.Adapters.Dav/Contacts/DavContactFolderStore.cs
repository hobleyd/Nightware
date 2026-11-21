using System.Xml.Linq;

using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Collections;
using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Application.Contacts;
using Nightware.Domain.Contacts;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Contacts;

/// <summary>
/// <see cref="IContactFolderStore"/> over CardDAV address books (ADR 0006 D7). Folders are every book in the
/// address-book home except the generated ones; the default book (<c>contacts</c>) is the parent of the rest
/// and is not listed, as Graph's Contacts folder is not. New books are <c>nightware-{guid}</c> by extended MKCOL.
/// </summary>
internal sealed class DavContactFolderStore(DavContactStore contacts, IDavPrincipalDiscovery discovery, IDavClient dav, DavPaths paths) : IContactFolderStore
{
    public const string WellKnownDefault = "contacts";

    public async Task<Result<IReadOnlyList<ContactFolder>>> ListAsync(MailboxRef mailbox, CancellationToken cancellationToken)
    {
        var books = await contacts.AddressBooksAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!books.IsSuccess)
        {
            return books.Error;
        }

        var root = DavContactStore.DefaultBook(books.Value);
        IReadOnlyList<ContactFolder> folders = [.. books.Value.Where(b => b != root).Select(b => ToFolder(b, root))];
        return Result<IReadOnlyList<ContactFolder>>.Success(folders);
    }

    public async Task<Result<ContactFolder>> GetAsync(MailboxRef mailbox, string id, CancellationToken cancellationToken)
    {
        var found = await FindAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        return found.IsSuccess ? ToFolder(found.Value.Book, found.Value.Root) : found.Error;
    }

    public async Task<Result<ContactFolder>> CreateAsync(MailboxRef mailbox, string displayName, CancellationToken cancellationToken)
    {
        var books = await contacts.AddressBooksAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!books.IsSuccess)
        {
            return books.Error;
        }

        if (books.Value.Any(b => string.Equals(b.DisplayName, displayName, StringComparison.OrdinalIgnoreCase)))
        {
            return Error.Conflict("A folder with the specified name already exists.", "displayName");
        }

        var principal = await discovery.DiscoverAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!principal.IsSuccess)
        {
            return principal.Error;
        }

        if (principal.Value.AddressBookHomeSet is not { } home || paths.ToRelative(home) is not { } homePath)
        {
            return Error.Forbidden("The mailbox has no address books.");
        }

        var href = $"{homePath.TrimEnd('/')}/nightware-{Guid.NewGuid():N}/";
        var body = DavRequestBodies.ExtendedMkCol([DavNames.Collection, DavNames.AddressBook], [new XElement(DavNames.DisplayName, displayName)]);
        var made = await dav.MkColAsync(href, body, cancellationToken).ConfigureAwait(false);
        if (!made.IsSuccess)
        {
            return made.Error;
        }

        var root = DavContactStore.DefaultBook(books.Value);
        return new ContactFolder(DavContactStore.FolderOf(href), displayName, RootId(root));
    }

    public async Task<Result<ContactFolder>> RenameAsync(MailboxRef mailbox, string id, string displayName, CancellationToken cancellationToken)
    {
        var found = await FindAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            return found.Error;
        }

        var patched = await dav.PropPatchAsync(found.Value.Book.Href, [new XElement(DavNames.DisplayName, displayName)], [], cancellationToken).ConfigureAwait(false);
        if (!patched.IsSuccess)
        {
            return patched.Error;
        }

        // Sabre answers 207 even when it refused the property; a refusal has a non-2xx propstat.
        if (patched.Value.Responses.SelectMany(r => r.PropStats).Any(p => !p.IsSuccess))
        {
            return Error.Forbidden("The folder cannot be renamed.");
        }

        return ToFolder(found.Value.Book with { DisplayName = displayName }, found.Value.Root);
    }

    public async Task<Result> DeleteAsync(MailboxRef mailbox, string id, CancellationToken cancellationToken)
    {
        var found = await FindAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        return found.IsSuccess ? await dav.DeleteAsync(found.Value.Book.Href, DavPrecondition.None, cancellationToken).ConfigureAwait(false) : found.Error;
    }

    private async Task<Result<(DavAddressBook Book, DavAddressBook? Root)>> FindAsync(MailboxRef mailbox, string id, CancellationToken cancellationToken)
    {
        var books = await contacts.AddressBooksAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!books.IsSuccess)
        {
            return books.Error;
        }

        var root = DavContactStore.DefaultBook(books.Value);
        var book = string.Equals(id, WellKnownDefault, StringComparison.OrdinalIgnoreCase)
            ? root
            : books.Value.FirstOrDefault(b => paths.ToRelative(b.Href) is { } path && DavContactStore.FolderOf(path).Value == id);
        return book is null ? Error.NotFound("The specified folder was not found in the store.", "id") : (book, root);
    }

    private ContactFolder ToFolder(DavAddressBook book, DavAddressBook? root) =>
        book == root
            ? new ContactFolder(Id(book), book.DisplayName, null, WellKnownDefault)
            : new ContactFolder(Id(book), book.DisplayName, RootId(root));

    private ContactFolderId? RootId(DavAddressBook? root) => root is null ? null : Id(root);

    private ContactFolderId Id(DavAddressBook book) => DavContactStore.FolderOf(paths.ToRelative(book.Href) ?? book.Href);
}
