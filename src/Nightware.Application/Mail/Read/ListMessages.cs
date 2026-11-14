using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail.Read;

/// <summary>
/// <c>GET mailFolders/{id}/messages</c> and <c>GET messages</c>: the folder page, the mailbox-wide
/// list, the conversation expansion (<c>$filter=conversationId …</c>) and <c>$search</c>. The
/// adapter decides which <c>$filter</c> shapes it can translate (a 400 lets the client fall back).
/// </summary>
public sealed class ListMessages(IMailMessageReader messages)
{
    public Task<Result<Page<MailMessage>>> ExecuteAsync(MailboxRef mailbox, FolderRef? folder, ItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Expand.Count > 0)
        {
            return Task.FromResult<Result<Page<MailMessage>>>(Error.Validation("$expand is not supported on a message collection.", "$expand"));
        }

        return messages.ListAsync(mailbox, folder, query, cancellationToken);
    }
}
