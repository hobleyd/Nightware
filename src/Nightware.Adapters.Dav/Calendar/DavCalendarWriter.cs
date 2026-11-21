using System.Globalization;
using Nightware.Adapters.Dav.Calendar.ICal;
using Nightware.Adapters.Dav.Core;
using Nightware.Application.Calendar;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Calendar;

/// <summary>
/// <see cref="ICalendarWriter"/> over CalDAV. Sabre's scheduling plugin turns every write into iTIP:
/// a new meeting invites its attendees, a reschedule (SEQUENCE bump) re-invites them, a deleted meeting
/// or occurrence is cancelled (organizer) or declined (attendee).
/// </summary>
internal sealed class DavCalendarWriter(
    DavCalendarScopes scopes, DavEventResources resources, DavCalendarReader reader, IDavClient dav, DavPaths paths, IcalNetCalendarCodec codec, IcsEditor editor,
    TimeProvider time)
    : ICalendarWriter
{
    /// <summary>
    /// A new event in the mailbox's default calendar. A brand-new resource is the one case where the
    /// iCalendar is built from Domain; the resource is named from a GUID, never the UID (README "Ids").
    /// </summary>
    public async Task<Result<CalendarEvent>> CreateAsync(MailboxRef mailbox, NewEvent newEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(newEvent);
        var scope = await scopes.ResolveAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!scope.IsSuccess)
        {
            return scope.Error;
        }

        if (scope.Value.Default is not { } calendar)
        {
            return Error.Forbidden($"{mailbox.Address} has no calendar you can add events to.");
        }

        return await CreateInAsync(calendar.Href, scope.Value.Owner.DisplayName, mailbox, newEvent, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A new event in the calendar at <paramref name="calendarHref"/>.</summary>
    internal async Task<Result<CalendarEvent>> CreateInAsync(string calendarHref, string? ownerName, MailboxRef mailbox, NewEvent newEvent, CancellationToken cancellationToken)
    {
        var href = calendarHref + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".ics";
        var now = time.GetUtcNow();
        var ics = codec.Serialize(new CalendarDocument(null, [ToEvent(newEvent, mailbox, ownerName) with { CreatedDateTime = now, LastModifiedDateTime = now }]));
        var put = await dav.PutAsync(href, ics, DavEventResources.ContentType, DavPrecondition.CreateOnly, cancellationToken).ConfigureAwait(false);
        if (!put.IsSuccess)
        {
            return put.Error;
        }

        // Sabre may rewrite what it stores (SCHEDULE-STATUS, room PARTSTAT), so answer with what it kept.
        return await reader.GetAsync(mailbox, DavItemId.Resource(paths.ToRelative(href)!).ToEventId(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<CalendarEvent>> UpdateAsync(MailboxRef mailbox, EventId id, EventPatch patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var stored = await resources.LoadAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        if (!stored.IsSuccess)
        {
            return stored.Error;
        }

        var edited = editor.ApplyPatch(stored.Value.Body, patch, stored.Value.Id.RecurrenceId, mailbox.Address);
        if (!edited.IsSuccess)
        {
            return edited.Error;
        }

        var saved = await resources.SaveAsync(stored.Value, edited.Value, cancellationToken).ConfigureAwait(false);
        if (!saved.IsSuccess)
        {
            return saved.Error;
        }

        return await reader.GetAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes the event, or for an occurrence id excludes that occurrence (EXDATE).</summary>
    public async Task<Result> DeleteAsync(MailboxRef mailbox, EventId id, CancellationToken cancellationToken)
    {
        var target = await resources.ResolveAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        if (!target.IsSuccess)
        {
            return target.Error;
        }

        if (!target.Value.IsOccurrence)
        {
            return await dav.DeleteAsync(target.Value.Href, DavPrecondition.None, cancellationToken).ConfigureAwait(false);
        }

        var stored = await resources.LoadAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        if (!stored.IsSuccess)
        {
            return stored.Error;
        }

        var edited = editor.ExcludeOccurrence(stored.Value.Body, target.Value.RecurrenceId!.Value);
        return edited.IsSuccess
            ? await resources.SaveAsync(stored.Value, edited.Value, cancellationToken).ConfigureAwait(false)
            : edited.Error;
    }

    /// <summary>The event as it will be stored. A meeting (one with attendees) is organized by the mailbox.</summary>
    private static CalendarEvent ToEvent(NewEvent newEvent, MailboxRef mailbox, string? displayName) =>
        new(new EventId(string.Empty), newEvent.Subject, newEvent.Start, newEvent.End)
        {
            ICalUId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
            IsAllDay = newEvent.IsAllDay,
            BodyHtml = newEvent.BodyHtml,
            Location = newEvent.Location,
            Locations = newEvent.Locations,
            Attendees = newEvent.Attendees
                .Where(a => !string.Equals(a.EmailAddress.Address, mailbox.Address, StringComparison.OrdinalIgnoreCase))
                .Select(a => a with { Status = new ResponseStatus(ResponseType.NotResponded) })
                .ToList(),
            Organizer = newEvent.Attendees.Count > 0 ? new EmailAddress(mailbox.Address, displayName) : null,
            ShowAs = newEvent.ShowAs,
            Recurrence = newEvent.Recurrence,
            IsReminderOn = newEvent.IsReminderOn,
            ReminderMinutesBeforeStart = newEvent.ReminderMinutesBeforeStart,
            Categories = newEvent.Categories,
        };
}
