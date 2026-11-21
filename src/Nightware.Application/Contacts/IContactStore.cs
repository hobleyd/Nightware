using Nightware.Application.Common.Query;
using Nightware.Domain.Contacts;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Contacts;

public interface IContactStore
{
    Task<Result<Page<Contact>>> ListAsync(MailboxRef mailbox, ItemQuery query, CancellationToken cancellationToken);

    /// <summary>One page of the contacts in one folder.</summary>
    Task<Result<Page<Contact>>> ListInAsync(MailboxRef mailbox, ContactFolderId folder, ItemQuery query, CancellationToken cancellationToken);

    Task<Result<Contact>> GetAsync(MailboxRef mailbox, ContactId id, CancellationToken cancellationToken);

    /// <param name="folder">The folder to create in; null for the default one.</param>
    Task<Result<Contact>> CreateAsync(MailboxRef mailbox, ContactFolderId? folder, ContactDraft draft, CancellationToken cancellationToken);

    /// <summary>Applies the non-null members of <paramref name="patch"/>.</summary>
    Task<Result<Contact>> UpdateAsync(MailboxRef mailbox, ContactId id, ContactDraft patch, CancellationToken cancellationToken);

    Task<Result> DeleteAsync(MailboxRef mailbox, ContactId id, CancellationToken cancellationToken);

    /// <summary>The contact's photo; <see cref="ErrorKind.NotFound"/> when it has none.</summary>
    Task<Result<BlobContent>> GetPhotoAsync(MailboxRef mailbox, ContactId id, CancellationToken cancellationToken);

    Task<Result> SetPhotoAsync(MailboxRef mailbox, ContactId id, byte[] photo, string contentType, CancellationToken cancellationToken);
}

/// <summary>
/// Contact folders (address books). <see cref="ListAsync"/> returns the folders below the default one, as
/// Graph's <c>contactFolders</c> does; the default folder is reachable by id or as <c>contacts</c>.
/// </summary>
public interface IContactFolderStore
{
    Task<Result<IReadOnlyList<ContactFolder>>> ListAsync(MailboxRef mailbox, CancellationToken cancellationToken);

    /// <param name="id">A folder id, or the well-known name <c>contacts</c>.</param>
    Task<Result<ContactFolder>> GetAsync(MailboxRef mailbox, string id, CancellationToken cancellationToken);

    Task<Result<ContactFolder>> CreateAsync(MailboxRef mailbox, string displayName, CancellationToken cancellationToken);

    Task<Result<ContactFolder>> RenameAsync(MailboxRef mailbox, string id, string displayName, CancellationToken cancellationToken);

    Task<Result> DeleteAsync(MailboxRef mailbox, string id, CancellationToken cancellationToken);
}
