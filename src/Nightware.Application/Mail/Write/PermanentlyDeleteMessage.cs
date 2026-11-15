using Nightware.Domain.Kernel;

namespace Nightware.Application.Mail.Write;

/// <summary>Graph <c>POST messages/{id}/permanentDelete</c> (empty folder, one message at a time).</summary>
public sealed class PermanentlyDeleteMessage(IMailMessageWriter writer)
{
    public Task<Result> ExecuteAsync(MailboxRef mailbox, MessageId id, CancellationToken cancellationToken) =>
        writer.PermanentDeleteAsync(mailbox, id, cancellationToken);
}
