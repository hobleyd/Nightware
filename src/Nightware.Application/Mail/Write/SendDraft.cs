using Nightware.Domain.Kernel;

namespace Nightware.Application.Mail.Write;

/// <summary>Graph <c>POST messages/{id}/send</c>: sends a draft and files it in Sent Items (202).</summary>
public sealed class SendDraft(IMailSender sender)
{
    public Task<Result> ExecuteAsync(MailboxRef mailbox, MessageId draftId, CancellationToken cancellationToken) =>
        sender.SendDraftAsync(mailbox, draftId, cancellationToken);
}
