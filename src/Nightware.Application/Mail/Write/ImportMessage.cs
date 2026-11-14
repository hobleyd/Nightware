using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail.Write;

/// <summary>
/// Graph <c>POST mailFolders/{id}/messages</c>: NightMail's account migration, which conveys the
/// original dates and read state through MAPI extended properties (ADR 0001 §36).
/// </summary>
public sealed class ImportMessage(IMailImporter importer, IMailMessageReader reader)
{
    public async Task<Result<MailMessage>> ExecuteAsync(MailboxRef mailbox, FolderRef folder, DraftContent content, ImportOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);

        var imported = await importer.ImportAsync(mailbox, folder, content, options, cancellationToken).ConfigureAwait(false);
        return imported.IsSuccess
            ? await WrittenMessage.ReadAsync(reader, mailbox, imported.Value, content, options.IsDraft, cancellationToken).ConfigureAwait(false)
            : imported.Error;
    }
}
