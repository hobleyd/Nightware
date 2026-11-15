using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail.Write;

/// <summary>Graph <c>POST mailFolders/{id}/move</c>, subfolders included; <c>msgfolderroot</c> moves it to the top level.</summary>
public sealed class MoveFolder(IMailFolderWriter folders)
{
    public Task<Result<MailFolder>> ExecuteAsync(MailboxRef mailbox, FolderId id, FolderRef destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return folders.MoveAsync(mailbox, id, destination, cancellationToken);
    }
}
