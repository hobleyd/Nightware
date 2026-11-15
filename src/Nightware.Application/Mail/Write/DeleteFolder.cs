using Nightware.Domain.Kernel;

namespace Nightware.Application.Mail.Write;

/// <summary>Graph <c>DELETE mailFolders/{id}</c>: the folder, its subfolders and their messages.</summary>
public sealed class DeleteFolder(IMailFolderWriter folders)
{
    public Task<Result> ExecuteAsync(MailboxRef mailbox, FolderId id, CancellationToken cancellationToken) =>
        folders.DeleteAsync(mailbox, id, cancellationToken);
}
