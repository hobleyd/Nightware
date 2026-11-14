using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Core.Collections;

/// <summary>Lists the calendar and address-book collections under a principal's home sets.</summary>
public interface IDavCollectionCatalog
{
    /// <summary>
    /// Calendars in the principal's calendar home (own and shared-with-me), excluding the
    /// scheduling inbox/outbox, trash bin, subscriptions and deleted calendars.
    /// </summary>
    Task<Result<IReadOnlyList<DavCalendar>>> ListCalendarsAsync(DavPrincipal principal, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<DavAddressBook>>> ListAddressBooksAsync(DavPrincipal principal, CancellationToken cancellationToken);
}
