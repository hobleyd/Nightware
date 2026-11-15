using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail.Write;

/// <summary>
/// Graph <c>POST messages/{id}/createForward</c>: saves the forward as a draft carrying copies of every
/// original attachment (201). The client then deletes the copies the user dropped and adds new files.
/// </summary>
public sealed class CreateForwardDraft(IMailMessageReader reader, IDraftStore drafts)
{
    public async Task<Result<MailMessage>> ExecuteAsync(MailboxRef mailbox, MessageId id, ComposedMessage composed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(composed);
        var original = await reader.GetAsync(mailbox, id, ReplyComposer.OriginalFetch, cancellationToken).ConfigureAwait(false);
        if (!original.IsSuccess)
        {
            return original.Error;
        }

        var content = ReplyComposer.Compose(original.Value, ReplyKind.Forward, composed, mailbox);
        var created = await drafts.CreateAsync(mailbox, content, cancellationToken).ConfigureAwait(false);
        return created.IsSuccess
            ? await WrittenMessage.ReadAsync(reader, mailbox, created.Value, content, isDraft: true, cancellationToken).ConfigureAwait(false)
            : created.Error;
    }
}
