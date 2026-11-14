using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Calendar.Events;

/// <summary>
/// Graph <c>PATCH events/{id}</c>. On an occurrence id it changes that occurrence only; when the
/// organizer moves a meeting the adapter re-invites the attendees (ADR 0001 §4).
/// </summary>
public sealed class UpdateEvent(ICalendarWriter writer)
{
    public async Task<Result<CalendarEvent>> ExecuteAsync(MailboxRef mailbox, EventId id, EventPatch patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var invalid = (patch is { Start: { } start, End: { } end }
                          ? CalendarRules.Ordered(start, end, "end")
                          : CalendarRules.KnownZone(patch.Start, "start") ?? CalendarRules.KnownZone(patch.End, "end"))
                      ?? CalendarRules.Addresses(patch.Attendees?.Select(a => a.EmailAddress) ?? [], "attendees");
        if (invalid is not null)
        {
            return invalid;
        }

        if (patch.ReminderMinutesBeforeStart < 0)
        {
            return Error.Validation("reminderMinutesBeforeStart cannot be negative.", "reminderMinutesBeforeStart");
        }

        return await writer.UpdateAsync(mailbox, id, patch, cancellationToken).ConfigureAwait(false);
    }
}
