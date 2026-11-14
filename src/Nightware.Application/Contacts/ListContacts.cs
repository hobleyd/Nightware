using Nightware.Application.Common.Query;
using Nightware.Domain.Contacts;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Contacts;

/// <summary>
/// <c>GET {mailbox}/contacts</c>: one page of the mailbox's personal contacts (the client's typeahead
/// cache, read-only). The store decides what of <see cref="ItemQuery"/> it can honour; the contract sends
/// only <c>$select</c>, <c>$top</c> and the <c>$skiptoken</c> of a previous page.
/// </summary>
public sealed class ListContacts(IContactStore contacts)
{
    public Task<Result<Page<Contact>>> ExecuteAsync(MailboxRef mailbox, ItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return contacts.ListAsync(mailbox, query, cancellationToken);
    }
}
