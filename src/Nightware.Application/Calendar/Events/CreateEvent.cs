using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Calendar.Events;

/// <summary>Graph <c>POST events</c>. Attendees are invited by the backend's scheduling (iTIP REQUEST).</summary>
public sealed class CreateEvent(ICalendarWriter writer)
{
    public async Task<Result<CalendarEvent>> ExecuteAsync(MailboxRef mailbox, NewEvent newEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(newEvent);
        var invalid = CalendarRules.Ordered(newEvent.Start, newEvent.End, "end")
                      ?? CalendarRules.Addresses(newEvent.Attendees.Select(a => a.EmailAddress), "attendees");
        if (invalid is not null)
        {
            return invalid;
        }

        if (newEvent.ReminderMinutesBeforeStart < 0)
        {
            return Error.Validation("reminderMinutesBeforeStart cannot be negative.", "reminderMinutesBeforeStart");
        }

        return await writer.CreateAsync(mailbox, newEvent, cancellationToken).ConfigureAwait(false);
    }
}
