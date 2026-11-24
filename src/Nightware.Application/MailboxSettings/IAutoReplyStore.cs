using Nightware.Domain.Kernel;
using Nightware.Domain.MailboxSettings;

namespace Nightware.Application.MailboxSettings;

/// <summary>Automatic replies (out of office), backed by JMAP <c>VacationResponse</c> or Nightware's Sieve script.</summary>
public interface IAutoReplyStore
{
    Task<Result<AutoReplySetting>> GetAsync(MailboxRef mailbox, CancellationToken cancellationToken);

    /// <param name="internalDomains">Senders from these domains get the internal reply (<see cref="IInternalDomains"/>).</param>
    /// <param name="knownSenders">
    /// For <c>contactsOnly</c>: outside senders who still get the external reply (<see cref="AutoReplyKnownSenders"/>).
    /// Null keeps the list the store last had (the admin console can't read contacts).
    /// </param>
    Task<Result<AutoReplySetting>> SetAsync(
        MailboxRef mailbox, AutoReplySetting setting, IReadOnlyList<string> internalDomains, IReadOnlyList<string>? knownSenders, CancellationToken cancellationToken);
}
