using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail.Write;

/// <summary>Graph <c>PATCH mailFolders/{id}</c> with <c>displayName</c>. The folder id survives the rename.</summary>
public sealed class RenameFolder(IMailFolderWriter folders)
{
    public Task<Result<MailFolder>> ExecuteAsync(MailboxRef mailbox, FolderId id, string? displayName, CancellationToken cancellationToken) =>
        FolderNames.IsValid(displayName)
            ? folders.RenameAsync(mailbox, id, displayName!.Trim(), cancellationToken)
            : Task.FromResult<Result<MailFolder>>(FolderNames.Invalid());
}
