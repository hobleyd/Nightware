using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail.Write;

/// <summary>Graph <c>POST messages</c>: saves a new draft in Drafts and returns it (201).</summary>
public sealed class CreateDraft(IDraftStore drafts, IMailMessageReader reader)
{
    public async Task<Result<MailMessage>> ExecuteAsync(MailboxRef mailbox, DraftContent content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var created = await drafts.CreateAsync(mailbox, content, cancellationToken).ConfigureAwait(false);
        return created.IsSuccess
            ? await WrittenMessage.ReadAsync(reader, mailbox, created.Value, content, isDraft: true, cancellationToken).ConfigureAwait(false)
            : created.Error;
    }
}
