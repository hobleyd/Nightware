using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail.Read;

/// <summary><c>GET mailFolders/{id|wellKnownName}</c>, including the shared-mailbox access probe.</summary>
public sealed class GetMailFolder(IMailFolderReader folders)
{
    public Task<Result<MailFolder>> ExecuteAsync(MailboxRef mailbox, FolderRef folder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return folders.GetAsync(mailbox, folder, cancellationToken);
    }
}
