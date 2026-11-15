using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail.Read;

/// <summary><c>GET messages/{id}/attachments</c>.</summary>
public sealed class ListMessageAttachments(IMailMessageReader messages)
{
    public Task<Result<IReadOnlyList<MailAttachment>>> ExecuteAsync(MailboxRef mailbox, MessageId id, CancellationToken cancellationToken) =>
        messages.ListAttachmentsAsync(mailbox, id, cancellationToken);
}
