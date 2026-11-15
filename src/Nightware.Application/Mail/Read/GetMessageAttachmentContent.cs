using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail.Read;

/// <summary><c>GET messages/{id}/attachments/{attachmentId}/$value</c>: the raw content (an attached message as MIME).</summary>
public sealed class GetMessageAttachmentContent(IMailMessageReader messages)
{
    public Task<Result<BlobContent>> ExecuteAsync(MailboxRef mailbox, MessageId id, AttachmentId attachment, CancellationToken cancellationToken) =>
        messages.GetAttachmentContentAsync(mailbox, id, attachment, cancellationToken);
}
