using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail.Write;

/// <summary>Graph <c>POST messages/{id}/reply</c> and <c>replyAll</c>: composes and sends at once (202).</summary>
public sealed class ReplyToMessage(IMailMessageReader reader, IMailSender sender)
{
    public async Task<Result> ExecuteAsync(MailboxRef mailbox, MessageId id, bool replyAll, ComposedMessage composed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(composed);
        var original = await reader.GetAsync(mailbox, id, ReplyComposer.OriginalFetch, cancellationToken).ConfigureAwait(false);
        if (!original.IsSuccess)
        {
            return original.Error;
        }

        var content = ReplyComposer.Compose(original.Value, replyAll ? ReplyKind.ReplyAll : ReplyKind.Reply, composed, mailbox);
        return await sender.SendAsync(mailbox, content, saveToSentItems: true, cancellationToken).ConfigureAwait(false);
    }
}
