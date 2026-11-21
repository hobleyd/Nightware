using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail.Write;

/// <summary>
/// Graph <c>POST mailFolders/{id}/copy</c>: the folder, its subfolders and their mail, copied under the destination;
/// <c>msgfolderroot</c> copies it to the top level.
/// </summary>
public sealed class CopyFolder(IMailFolderWriter folders)
{
    public Task<Result<MailFolder>> ExecuteAsync(MailboxRef mailbox, FolderId id, FolderRef destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return folders.CopyAsync(mailbox, id, destination, cancellationToken);
    }
}
