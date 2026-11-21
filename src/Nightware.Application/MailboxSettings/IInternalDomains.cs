using Nightware.Domain.Kernel;

namespace Nightware.Application.MailboxSettings;

/// <summary>
/// The mail domains whose senders count as internal for a mailbox's automatic replies (Graph's "inside your
/// organization"): the domains of the entity that owns the mailbox's domain.
/// </summary>
public interface IInternalDomains
{
    /// <returns>Lower-case domains, always including the mailbox's own.</returns>
    Task<IReadOnlyList<string>> ForAsync(MailboxRef mailbox, CancellationToken cancellationToken);
}
