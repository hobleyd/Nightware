using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Calendar.Events;

/// <summary>Graph <c>GET events/{id}</c>: a single event, series master or occurrence.</summary>
public sealed class GetEvent(ICalendarReader reader)
{
    public Task<Result<CalendarEvent>> ExecuteAsync(MailboxRef mailbox, EventId id, CancellationToken cancellationToken) =>
        reader.GetAsync(mailbox, id, cancellationToken);
}
