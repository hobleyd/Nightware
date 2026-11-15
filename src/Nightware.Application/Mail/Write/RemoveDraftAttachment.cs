using Nightware.Domain.Kernel;

namespace Nightware.Application.Mail.Write;

/// <summary>Graph <c>DELETE messages/{id}/attachments/{attachmentId}</c> on a draft. The draft keeps its id.</summary>
public sealed class RemoveDraftAttachment(IDraftStore drafts)
{
    public Task<Result> ExecuteAsync(MailboxRef mailbox, MessageId draftId, AttachmentId attachment, CancellationToken cancellationToken) =>
        drafts.RemoveAttachmentAsync(mailbox, draftId, attachment, cancellationToken);
}
