using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail.Read;

/// <summary><c>GET mailFolders/delta</c>: one page of the folder hierarchy's delta feed.</summary>
public sealed class SyncFolders(IMailFolderDeltaReader delta)
{
    /// <param name="token">Null starts an initial sync.</param>
    public Task<Result<DeltaPage<MailFolder>>> ExecuteAsync(MailboxRef mailbox, DeltaToken? token, DeltaRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PageSize <= 0)
        {
            return Task.FromResult<Result<DeltaPage<MailFolder>>>(Error.Validation("$top must be positive.", "$top"));
        }

        return delta.DeltaAsync(mailbox, token, request, cancellationToken);
    }
}
