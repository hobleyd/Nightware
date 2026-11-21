using Nightware.Application.Common.Query;
using Nightware.Domain.Contacts;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Contacts;

/// <summary>
/// <c>{mailbox}/contactFolders</c> (ADR 0006 D7): address books. CardDAV is flat, so a folder has no child
/// folders; the default folder is neither renamed nor deleted.
/// </summary>
public sealed class ContactFoldersUseCase(IContactFolderStore folders, IContactStore contacts)
{
    public Task<Result<IReadOnlyList<ContactFolder>>> ListAsync(MailboxRef mailbox, CancellationToken cancellationToken) =>
        folders.ListAsync(mailbox, cancellationToken);

    public Task<Result<ContactFolder>> GetAsync(MailboxRef mailbox, string id, CancellationToken cancellationToken) =>
        folders.GetAsync(mailbox, id, cancellationToken);

    public Task<Result<ContactFolder>> CreateAsync(MailboxRef mailbox, string? displayName, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(displayName)
            ? Task.FromResult<Result<ContactFolder>>(Error.Validation("displayName is required.", "displayName"))
            : folders.CreateAsync(mailbox, displayName.Trim(), cancellationToken);

    public async Task<Result<ContactFolder>> RenameAsync(MailboxRef mailbox, string id, string? displayName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return Error.Validation("displayName is required.", "displayName");
        }

        var folder = await folders.GetAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        if (!folder.IsSuccess)
        {
            return folder.Error;
        }

        return folder.Value.WellKnownName is not null
            ? Error.Validation("The default contact folder cannot be renamed.", "id")
            : await folders.RenameAsync(mailbox, id, displayName.Trim(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result> DeleteAsync(MailboxRef mailbox, string id, CancellationToken cancellationToken)
    {
        var folder = await folders.GetAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        if (!folder.IsSuccess)
        {
            return folder.Error;
        }

        return folder.Value.WellKnownName is not null
            ? Error.Validation("The default contact folder cannot be deleted.", "id")
            : await folders.DeleteAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Always empty: an address book holds no address books. An unknown folder is still 404.</summary>
    public async Task<Result<IReadOnlyList<ContactFolder>>> ChildFoldersAsync(MailboxRef mailbox, string id, CancellationToken cancellationToken)
    {
        var folder = await folders.GetAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        return folder.IsSuccess ? Result<IReadOnlyList<ContactFolder>>.Success([]) : folder.Error;
    }

    public async Task<Result<Page<Contact>>> ContactsAsync(MailboxRef mailbox, string id, ItemQuery query, CancellationToken cancellationToken)
    {
        var folder = await folders.GetAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        return folder.IsSuccess ? await contacts.ListInAsync(mailbox, folder.Value.Id, query, cancellationToken).ConfigureAwait(false) : folder.Error;
    }

    public async Task<Result<Contact>> CreateContactAsync(MailboxRef mailbox, string id, ContactDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var folder = await folders.GetAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        return folder.IsSuccess
            ? await new CreateContact(contacts).ExecuteAsync(mailbox, folder.Value.Id, draft, cancellationToken).ConfigureAwait(false)
            : folder.Error;
    }
}
