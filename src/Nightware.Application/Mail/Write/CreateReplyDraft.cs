using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail.Write;

/// <summary>
/// Graph <c>POST messages/{id}/createReply</c> and <c>createReplyAll</c>: saves the reply as a draft (201)
/// so the client can attach files before sending it.
/// </summary>
public sealed class CreateReplyDraft(IMailMessageReader reader, IDraftStore drafts)
{
    public async Task<Result<MailMessage>> ExecuteAsync(MailboxRef mailbox, MessageId id, bool replyAll, ComposedMessage composed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(composed);
        var original = await reader.GetAsync(mailbox, id, ReplyComposer.OriginalFetch, cancellationToken).ConfigureAwait(false);
        if (!original.IsSuccess)
        {
            return original.Error;
        }

        var content = ReplyComposer.Compose(original.Value, replyAll ? ReplyKind.ReplyAll : ReplyKind.Reply, composed, mailbox);
        var created = await drafts.CreateAsync(mailbox, content, cancellationToken).ConfigureAwait(false);
        return created.IsSuccess
            ? await WrittenMessage.ReadAsync(reader, mailbox, created.Value, content, isDraft: true, cancellationToken).ConfigureAwait(false)
            : created.Error;
    }
}
