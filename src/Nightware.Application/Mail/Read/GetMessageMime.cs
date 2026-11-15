using Nightware.Domain.Kernel;

namespace Nightware.Application.Mail.Read;

/// <summary><c>GET messages/{id}/$value</c>: the message as raw RFC 5322 bytes.</summary>
public sealed class GetMessageMime(IMailMessageReader messages)
{
    public Task<Result<BlobContent>> ExecuteAsync(MailboxRef mailbox, MessageId id, CancellationToken cancellationToken) =>
        messages.GetRawAsync(mailbox, id, cancellationToken);
}
