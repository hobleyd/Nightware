using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail.Read;

/// <summary>
/// <c>GET mailFolders</c> (top-level folders, <paramref name="parent"/> null) and
/// <c>GET mailFolders/{id}/childFolders</c>.
/// </summary>
public sealed class ListMailFolders(IMailFolderReader folders)
{
    public Task<Result<Page<MailFolder>>> ExecuteAsync(MailboxRef mailbox, FolderRef? parent, ItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return folders.ListAsync(mailbox, parent, query, cancellationToken);
    }
}
