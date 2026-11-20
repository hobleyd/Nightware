using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail.Write;

/// <summary>Graph <c>POST messages/{id}/copy</c>: answers 201 with the copy, which has an id of its own.</summary>
public sealed class CopyMessage(IMailMessageWriter writer, IMailMessageReader reader)
{
    public async Task<Result<MailMessage>> ExecuteAsync(MailboxRef mailbox, MessageId id, FolderRef destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var copied = await writer.CopyAsync(mailbox, id, destination, cancellationToken).ConfigureAwait(false);
        return copied.IsSuccess
            ? await reader.GetAsync(mailbox, copied.Value, MessageFetch.Default, cancellationToken).ConfigureAwait(false)
            : copied.Error;
    }
}
