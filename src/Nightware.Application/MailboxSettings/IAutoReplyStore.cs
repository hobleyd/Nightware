using Nightware.Domain.Kernel;
using Nightware.Domain.MailboxSettings;

namespace Nightware.Application.MailboxSettings;

/// <summary>Automatic replies (out of office), backed by JMAP <c>VacationResponse</c> or Nightware's Sieve script.</summary>
public interface IAutoReplyStore
{
    Task<Result<AutoReplySetting>> GetAsync(MailboxRef mailbox, CancellationToken cancellationToken);

    /// <param name="internalDomains">Senders from these domains get the internal reply (<see cref="IInternalDomains"/>).</param>
    Task<Result<AutoReplySetting>> SetAsync(MailboxRef mailbox, AutoReplySetting setting, IReadOnlyList<string> internalDomains, CancellationToken cancellationToken);
}
