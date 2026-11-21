using Nightware.Adapters.Dav.Calendar.ICal;
using Nightware.Application.Calendar.Calendars;
using Nightware.Application.Common.Time;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Calendar;

/// <summary>Snooze and dismiss as RFC 9074 VALARM edits on the event's resource, saved with its ETag.</summary>
internal sealed class DavCalendarReminders(DavEventResources resources, DavCalendarReader reader, IcsEditor editor) : ICalendarReminders
{
    public Task<Result> SnoozeAsync(MailboxRef mailbox, EventId id, DateTimeOffset until, CancellationToken cancellationToken) =>
        EditAsync(mailbox, id, ics => editor.SnoozeReminder(ics, until), cancellationToken);

    public async Task<Result> DismissAsync(MailboxRef mailbox, EventId id, CancellationToken cancellationToken)
    {
        // The occurrence's own fire time, so a reminder dismissed before it fires stays dismissed.
        var occurrence = await reader.GetAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        if (!occurrence.IsSuccess)
        {
            return occurrence.Error;
        }

        var e = occurrence.Value;
        var due = GraphTimeZone.ToInstant(e.Start.DateTime, e.Start.TimeZone).AddMinutes(-e.ReminderMinutesBeforeStart);
        return await EditAsync(mailbox, id, ics => editor.DismissReminder(ics, due), cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result> EditAsync(MailboxRef mailbox, EventId id, Func<string, Result<string>> edit, CancellationToken cancellationToken)
    {
        var stored = await resources.LoadAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        if (!stored.IsSuccess)
        {
            return stored.Error;
        }

        var edited = edit(stored.Value.Body);
        return edited.IsSuccess ? await resources.SaveAsync(stored.Value, edited.Value, cancellationToken).ConfigureAwait(false) : edited.Error;
    }
}
