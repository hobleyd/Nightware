using Nightware.Application.Common.Query;
using Nightware.Application.Common.Time;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Calendar.Calendars;

/// <summary>Reminder state on an event, kept in its VALARMs (RFC 9074; ADR 0006 D6).</summary>
public interface ICalendarReminders
{
    Task<Result> SnoozeAsync(MailboxRef mailbox, EventId id, DateTimeOffset until, CancellationToken cancellationToken);

    Task<Result> DismissAsync(MailboxRef mailbox, EventId id, CancellationToken cancellationToken);
}

/// <summary>
/// Graph <c>reminderView(startDateTime, endDateTime)</c>: the reminders of the default calendar's occurrences that
/// start in the window and are not dismissed, firing at the snooze time when snoozed.
/// </summary>
public sealed class GetReminderView(ICalendarReader reader)
{
    public async Task<Result<IReadOnlyList<Reminder>>> ExecuteAsync(MailboxRef mailbox, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        if (end < start)
        {
            return Error.Validation("endDateTime is before startDateTime.", "endDateTime");
        }

        if (CalendarViewLimits.Check(start, end) is { } tooLong)
        {
            return tooLong;
        }

        var view = await reader.CalendarViewAsync(mailbox, start, end, new ItemQuery { Top = int.MaxValue }, cancellationToken).ConfigureAwait(false);
        if (!view.IsSuccess)
        {
            return view.Error;
        }

        var reminders = new List<Reminder>();
        foreach (var e in view.Value.Items.Where(static e => e.IsReminderOn && !e.IsCancelled))
        {
            var eventStart = GraphTimeZone.ToInstant(e.Start.DateTime, e.Start.TimeZone);
            var fires = e.ReminderSnoozedUntil ?? eventStart.AddMinutes(-e.ReminderMinutesBeforeStart);
            if (e.ReminderSnoozedUntil is null && e.ReminderAcknowledged is { } acknowledged && acknowledged >= fires)
            {
                continue;
            }

            reminders.Add(new Reminder(e.Id, e.Subject, e.Start, e.End, DateTimeTimeZone.Utc(fires)) { EventLocation = e.Location });
        }

        return reminders.OrderBy(static r => r.ReminderFireTime.DateTime).ToList();
    }
}

/// <summary>Graph <c>POST events/{id}/snoozeReminder</c>.</summary>
public sealed class SnoozeReminder(ICalendarReminders reminders)
{
    public Task<Result> ExecuteAsync(MailboxRef mailbox, EventId id, DateTimeTimeZone? newReminderTime, CancellationToken cancellationToken) =>
        newReminderTime is null
            ? Task.FromResult<Result>(Error.Validation("newReminderTime is required.", "newReminderTime"))
            : reminders.SnoozeAsync(mailbox, id, GraphTimeZone.ToInstant(newReminderTime.DateTime, newReminderTime.TimeZone), cancellationToken);
}

/// <summary>Graph <c>POST events/{id}/dismissReminder</c>.</summary>
public sealed class DismissReminder(ICalendarReminders reminders)
{
    public Task<Result> ExecuteAsync(MailboxRef mailbox, EventId id, CancellationToken cancellationToken) =>
        reminders.DismissAsync(mailbox, id, cancellationToken);
}
