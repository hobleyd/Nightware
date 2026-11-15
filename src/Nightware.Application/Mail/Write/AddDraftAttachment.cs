using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail.Write;

/// <summary>Graph <c>POST messages/{id}/attachments</c> on a draft. The draft keeps its id.</summary>
public sealed class AddDraftAttachment(IDraftStore drafts)
{
    public Task<Result<MailAttachment>> ExecuteAsync(MailboxRef mailbox, MessageId draftId, NewAttachment attachment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        if (string.IsNullOrWhiteSpace(attachment.Name))
        {
            return Task.FromResult<Result<MailAttachment>>(Error.Validation("An attachment needs a name.", "name"));
        }

        return drafts.AddAttachmentAsync(mailbox, draftId, attachment, cancellationToken);
    }
}
