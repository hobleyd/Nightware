using Nightware.Domain.Kernel;

namespace Nightware.Application.Mail.Write;

/// <summary>
/// Graph <c>DELETE messages/{id}</c>: moves the message to Deleted Items, or destroys it when it is
/// already there. NightMail only uses it to discard a server draft.
/// </summary>
public sealed class DeleteMessage(IMailMessageWriter writer)
{
    public Task<Result> ExecuteAsync(MailboxRef mailbox, MessageId id, CancellationToken cancellationToken) =>
        writer.SoftDeleteAsync(mailbox, id, cancellationToken);
}
