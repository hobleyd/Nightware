using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail.Read;

/// <summary>
/// <c>GET messages/{id}/attachments/{attachmentId}</c>: the full attachment. A file attachment
/// carries its bytes (<c>contentBytes</c>); an item attachment never does — the client then
/// fetches <c>$value</c>, as it does against Graph.
/// </summary>
public sealed class GetMessageAttachment(IMailMessageReader messages)
{
    public Task<Result<MailAttachment>> ExecuteAsync(MailboxRef mailbox, MessageId id, AttachmentId attachment, CancellationToken cancellationToken) =>
        messages.GetAttachmentAsync(mailbox, id, attachment, includeContent: true, cancellationToken);
}
