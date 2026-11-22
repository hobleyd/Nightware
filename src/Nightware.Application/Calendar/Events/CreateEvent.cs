using Nightware.Application.Calendar.OnlineMeetings;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Calendar.Events;

/// <summary>
/// Graph <c>POST events</c>. Attendees are invited by the backend's scheduling (iTIP REQUEST); an online
/// meeting gets its room first, so the invitation carries the join URL.
/// </summary>
public sealed class CreateEvent(ICalendarWriter writer, OnlineMeetingRooms rooms)
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

        var online = await rooms.OpenForAsync(newEvent, cancellationToken).ConfigureAwait(false);
        var created = await writer.CreateAsync(mailbox, online, cancellationToken).ConfigureAwait(false);
        if (!created.IsSuccess)
        {
            await rooms.DiscardAsync(newEvent, online, cancellationToken).ConfigureAwait(false);
        }

        return created;
    }
}
