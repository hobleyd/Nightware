using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail.Write;

/// <summary>Graph <c>POST messages/{id}/forward</c>: sends the composed forward with all the original's attachments (202).</summary>
public sealed class ForwardMessage(IMailMessageReader reader, IMailSender sender)
{
    public async Task<Result> ExecuteAsync(MailboxRef mailbox, MessageId id, ComposedMessage composed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(composed);
        var original = await reader.GetAsync(mailbox, id, ReplyComposer.OriginalFetch, cancellationToken).ConfigureAwait(false);
        if (!original.IsSuccess)
        {
            return original.Error;
        }

        var content = ReplyComposer.Compose(original.Value, ReplyKind.Forward, composed, mailbox);
        return await sender.SendAsync(mailbox, content, saveToSentItems: true, cancellationToken).ConfigureAwait(false);
    }
}
