using Ical.Net;
using Ical.Net.Serialization;
using Nightware.Adapters.Dav.Core;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using IcalCalendar = Ical.Net.Calendar;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace Nightware.Adapters.Dav.Calendar.ICal;

/// <summary>
/// Targeted edits of a stored calendar resource that keep every property the Domain model
/// cannot carry (X- properties, ATTACH, SCHEDULE-* parameters, VALARMs, attendee comments).
/// Use these instead of regenerating the resource from Domain when only one aspect changes.
/// Each edit refreshes DTSTAMP/LAST-MODIFIED on the components it touches.
/// </summary>
public sealed class IcsEditor
{
    private readonly TimeProvider _timeProvider;

    public IcsEditor(TimeProvider timeProvider) =>
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <summary>
    /// "Delete this occurrence": adds an EXDATE for <paramref name="occurrence"/> to the series
    /// master (written in the master's own DTSTART form) and drops a matching exception VEVENT.
    /// </summary>
    public Result<string> ExcludeOccurrence(string ics, DavRecurrenceId occurrence) =>
        Edit(ics, calendar =>
        {
            var master = Master(calendar);
            if (master?.DtStart is null || master.RecurrenceRule is null)
            {
                return Error.NotFound("The event is not a recurring series.");
            }

            var zones = new IcalZoneContext(calendar, "UTC");
            var instant = occurrence.ToUtc();
            foreach (var exception in calendar.Events.Where(e => e.RecurrenceIdentifier?.StartTime is not null && SameInstance(zones, e.RecurrenceIdentifier!.StartTime, occurrence)).ToList())
            {
                calendar.Events.Remove(exception);
            }

            var value = master.DtStart.HasTime
                ? master.DtStart is { IsUtc: false, IsFloating: false } zoned
                    ? new Ical.Net.DataTypes.CalDateTime(IcalZoneContext.ToWallClock(instant, zones.ZoneNameOf(zoned) ?? "UTC"), zoned.TzId, true)
                    : IcalNetCalendarCodec.Utc(instant)
                : new Ical.Net.DataTypes.CalDateTime(occurrence.IsDate ? occurrence.ToDate() : DateOnly.FromDateTime(instant.UtcDateTime));
            master.ExceptionDates.Add(value);
            Touch(master);
            return Result.Success();
        });

    /// <summary>
    /// Sets the PARTSTAT of <paramref name="attendeeAddress"/> on the master and every
    /// exception (or only on the exception for <paramref name="occurrence"/>). Removes RSVP
    /// and, when <paramref name="comment"/> is given, records it as the component's COMMENT.
    /// </summary>
    public Result<string> SetParticipation(string ics, string attendeeAddress, RsvpResponse response, DavRecurrenceId? occurrence = null, string? comment = null) =>
        Edit(ics, calendar =>
        {
            var zones = new IcalZoneContext(calendar, "UTC");
            var targets = occurrence is { } rid
                ? calendar.Events.Where(e => e.RecurrenceIdentifier?.StartTime is not null && SameInstance(zones, e.RecurrenceIdentifier!.StartTime, rid)).ToList()
                : calendar.Events.ToList();
            var partStat = response switch
            {
                RsvpResponse.Accept => EventParticipationStatus.Accepted,
                RsvpResponse.Decline => EventParticipationStatus.Declined,
                _ => EventParticipationStatus.Tentative,
            };
            var changed = false;
            foreach (var vevent in targets)
            {
                foreach (var attendee in vevent.Attendees.Where(a => string.Equals(IcalNetCalendarCodec.AddressOf(a.Value), attendeeAddress.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    attendee.ParticipationStatus = partStat;
                    attendee.Rsvp = false;
                    changed = true;
                }

                if (changed && comment is not null)
                {
                    vevent.Comments = [comment];
                }

                Touch(vevent);
            }

            return changed ? Result.Success() : Error.NotFound("The mailbox is not an attendee of this event.");
        });

    /// <summary>Organizer cancel: STATUS:CANCELLED and SEQUENCE+1 on every component, so Sabre sends iTIP CANCEL.</summary>
    public Result<string> Cancel(string ics) =>
        Edit(ics, calendar =>
        {
            foreach (var vevent in calendar.Events)
            {
                vevent.Status = EventStatus.Cancelled;
                vevent.Sequence++;
                Touch(vevent);
            }

            return Result.Success();
        });

    /// <summary>SEQUENCE+1 on every component (organizer changed the schedule; ADR 0001 §4).</summary>
    public Result<string> BumpSequence(string ics) =>
        Edit(ics, calendar =>
        {
            foreach (var vevent in calendar.Events)
            {
                vevent.Sequence++;
                Touch(vevent);
            }

            return Result.Success();
        });

    private static Result<string> Edit(string ics, Func<IcalCalendar, Result> edit)
    {
        var loaded = IcalNetCalendarCodec.Load(ics);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        var outcome = edit(loaded.Value);
        return outcome.IsSuccess
            ? new CalendarSerializer().SerializeToString(loaded.Value) ?? string.Empty
            : outcome.Error;
    }

    private void Touch(IcalEvent vevent)
    {
        var now = IcalNetCalendarCodec.Utc(_timeProvider.GetUtcNow());
        vevent.DtStamp = now;
        vevent.LastModified = now;
    }

    private static IcalEvent? Master(IcalCalendar calendar) => calendar.Events.FirstOrDefault(e => e.RecurrenceIdentifier?.StartTime is null);

    private static bool SameInstance(IcalZoneContext zones, Ical.Net.DataTypes.CalDateTime recurrenceId, DavRecurrenceId wanted) =>
        recurrenceId.HasTime
            ? !wanted.IsDate && zones.ToInstant(recurrenceId) == wanted.ToUtc()
            : wanted.IsDate && recurrenceId.Date == wanted.ToDate();
}
