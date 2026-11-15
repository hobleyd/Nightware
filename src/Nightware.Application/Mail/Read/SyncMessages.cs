using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail.Read;

/// <summary><c>GET mailFolders/{id}/messages/delta</c>: one page of the folder's delta feed (ADR 0001 §6–8).</summary>
public sealed class SyncMessages(IMailDeltaReader delta)
{
    /// <param name="token">Null starts an initial sync.</param>
    public Task<Result<DeltaPage<MailMessage>>> ExecuteAsync(MailboxRef mailbox, FolderRef folder, DeltaToken? token, DeltaRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(request);
        if (request.PageSize <= 0)
        {
            return Task.FromResult<Result<DeltaPage<MailMessage>>>(Error.Validation("$top must be positive.", "$top"));
        }

        return delta.DeltaAsync(mailbox, folder, token, request, cancellationToken);
    }
}
