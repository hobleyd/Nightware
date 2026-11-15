using Nightware.Application.Common.Query;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Calendar.Events;

/// <summary>
/// Graph <c>GET events</c>: unexpanded events (single instances and series masters). NightMail only
/// ever filters by <c>iCalUId</c>; the adapter rejects filters it cannot translate with a 400.
/// </summary>
public sealed class ListEvents(ICalendarReader reader)
{
    public Task<Result<Page<CalendarEvent>>> ExecuteAsync(MailboxRef mailbox, ItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return reader.ListAsync(mailbox, query, cancellationToken);
    }
}
