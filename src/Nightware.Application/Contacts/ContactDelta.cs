using Nightware.Application.Common.Delta;
using Nightware.Domain.Contacts;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Contacts;

/// <summary>Changes to the contacts of one folder since a token (the adapter's own).</summary>
public interface IContactDelta
{
    /// <param name="token">Null for the initial sync: every contact in the folder, and a token for what follows.</param>
    Task<Result<DeltaPage<Contact>>> DeltaAsync(MailboxRef mailbox, ContactFolderId folder, DeltaToken? token, CancellationToken cancellationToken);
}

/// <summary>Graph <c>GET contactFolders/{id}/contacts/delta</c>.</summary>
public sealed class SyncFolderContacts(IContactFolderStore folders, IContactDelta delta)
{
    public async Task<Result<DeltaPage<Contact>>> ExecuteAsync(MailboxRef mailbox, string folderId, DeltaToken? token, CancellationToken cancellationToken)
    {
        var folder = await folders.GetAsync(mailbox, folderId, cancellationToken).ConfigureAwait(false);
        return folder.IsSuccess ? await delta.DeltaAsync(mailbox, folder.Value.Id, token, cancellationToken).ConfigureAwait(false) : folder.Error;
    }
}

/// <summary>
/// Graph <c>GET contactFolders/delta</c>. An address-book home has no sync token of its own, so the token is a
/// snapshot of the folders (<see cref="SnapshotDelta"/>).
/// </summary>
public sealed class SyncContactFolders(IContactFolderStore folders)
{
    public async Task<Result<DeltaPage<ContactFolder>>> ExecuteAsync(MailboxRef mailbox, DeltaToken? token, CancellationToken cancellationToken)
    {
        var current = await folders.ListAsync(mailbox, cancellationToken).ConfigureAwait(false);
        return current.IsSuccess ? SnapshotDelta.Diff(current.Value, token, f => f.Id.Value, f => f.DisplayName) : current.Error;
    }
}
