using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail.Write;

/// <summary>
/// Graph <c>POST mailFolders/{id}/childFolders</c>; NightMail creates top-level folders under
/// <c>msgfolderroot</c>. A name already used under the parent is a 409, which the client takes as
/// "already exists".
/// </summary>
public sealed class CreateChildFolder(IMailFolderWriter folders)
{
    public Task<Result<MailFolder>> ExecuteAsync(MailboxRef mailbox, FolderRef parent, string? displayName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return FolderNames.IsValid(displayName)
            ? folders.CreateAsync(mailbox, parent, displayName!.Trim(), cancellationToken)
            : Task.FromResult<Result<MailFolder>>(FolderNames.Invalid());
    }
}
