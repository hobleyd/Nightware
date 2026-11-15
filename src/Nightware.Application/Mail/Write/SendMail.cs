using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail.Write;

/// <summary>Graph <c>POST sendMail</c>: sends a new message, with its attachments inline in the request (202).</summary>
public sealed class SendMail(IMailSender sender)
{
    public Task<Result> ExecuteAsync(MailboxRef mailbox, DraftContent content, bool saveToSentItems, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        return sender.SendAsync(mailbox, content, saveToSentItems, cancellationToken);
    }
}
