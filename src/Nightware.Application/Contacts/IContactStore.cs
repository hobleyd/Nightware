using Nightware.Application.Common.Query;
using Nightware.Domain.Contacts;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Contacts;

public interface IContactStore
{
    Task<Result<Page<Contact>>> ListAsync(MailboxRef mailbox, ItemQuery query, CancellationToken cancellationToken);

    Task<Result<Contact>> GetAsync(MailboxRef mailbox, ContactId id, CancellationToken cancellationToken);

    Task<Result<Contact>> CreateAsync(MailboxRef mailbox, ContactDraft draft, CancellationToken cancellationToken);

    /// <summary>Applies the non-null members of <paramref name="patch"/>.</summary>
    Task<Result<Contact>> UpdateAsync(MailboxRef mailbox, ContactId id, ContactDraft patch, CancellationToken cancellationToken);

    Task<Result> DeleteAsync(MailboxRef mailbox, ContactId id, CancellationToken cancellationToken);
}
