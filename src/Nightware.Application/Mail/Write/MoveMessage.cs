using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail.Write;

/// <summary>
/// Graph <c>POST messages/{id}/move</c>: delete (to Deleted Items), report junk, not junk. Answers 201 with
/// the moved message, whose id is unchanged (ADR 0001 §2).
/// </summary>
public sealed class MoveMessage(IMailMessageWriter writer, IMailMessageReader reader)
{
    public async Task<Result<MailMessage>> ExecuteAsync(MailboxRef mailbox, MessageId id, FolderRef destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var moved = await writer.MoveAsync(mailbox, id, destination, cancellationToken).ConfigureAwait(false);
        return moved.IsSuccess
            ? await reader.GetAsync(mailbox, moved.Value, MessageFetch.Default, cancellationToken).ConfigureAwait(false)
            : moved.Error;
    }
}
