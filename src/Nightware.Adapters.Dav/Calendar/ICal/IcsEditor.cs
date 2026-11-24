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
public sealed partial class IcsEditor
{
    private readonly TimeProvider _timeProvider;

    private const string ScheduleAgent = "SCHEDULE-AGENT";

    public IcsEditor(TimeProvider timeProvider) =>
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <summary>
    /// The attendee's iTIP answer as its own object (RFC 5546 §3.2.3, §3.2.7) for mailing to an organizer the server
    /// can't reach with it: <c>REPLY</c>, or <c>COUNTER</c> with <paramref name="proposed"/> as the new times. It
    /// carries the meeting's UID, SEQUENCE, ORGANIZER and (for one occurrence) RECURRENCE-ID, the attendee alone with
    /// their PARTSTAT, and the comment.
    /// </summary>
    public Result<string> Reply(string ics, string attendeeAddress, RsvpResponse response, DavRecurrenceId? occurrence, string? comment, TimeSlot? proposed)
    {
        var loaded = IcalNetCalendarCodec.Load(ics);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        var stored = loaded.Value;
        var zones = new IcalZoneContext(stored, "UTC");
        var source = occurrence is { } rid
            ? stored.Events.FirstOrDefault(e => e.RecurrenceIdentifier?.StartTime is { } start && SameInstance(zones, start, rid)) ?? Master(stored)
            : Master(stored) ?? stored.Events.FirstOrDefault();
        var me = source?.Attendees.FirstOrDefault(a => string.Equals(IcalNetCalendarCodec.AddressOf(a.Value), attendeeAddress.Trim(), StringComparison.OrdinalIgnoreCase));
        if (source is null || me is null || source.Organizer is null)
        {
            return Error.NotFound("The mailbox is not an attendee of this event.");
        }

        var method = proposed is null ? "REPLY" : "COUNTER";
        var reply = new IcalCalendar { Method = method, ProductId = "-//Nightware//Graph façade//EN" };
        var vevent = new IcalEvent
        {
            Uid = source.Uid,
            Sequence = source.Sequence,
            Summary = source.Summary,
            DtStamp = IcalNetCalendarCodec.Utc(_timeProvider.GetUtcNow()),
            Organizer = new Ical.Net.DataTypes.Organizer(source.Organizer.Value?.ToString() ?? string.Empty) { CommonName = source.Organizer.CommonName },
        };
        if (occurrence is not null && source.RecurrenceIdentifier is { } recurrence)
        {
            vevent.RecurrenceIdentifier = recurrence;
        }

        if (proposed is { } slot)
        {
            vevent.DtStart = IcalNetCalendarCodec.Utc(GraphTimeZoneInstant(slot.Start));
            vevent.DtEnd = IcalNetCalendarCodec.Utc(GraphTimeZoneInstant(slot.End));
        }
        else
        {
            vevent.DtStart = source.DtStart;
            vevent.DtEnd = source.DtEnd;
        }

        vevent.Attendees.Add(new Ical.Net.DataTypes.Attendee(me.Value?.ToString() ?? string.Empty)
        {
            CommonName = me.CommonName,
            ParticipationStatus = response switch
            {
                RsvpResponse.Accept => EventParticipationStatus.Accepted,
                RsvpResponse.Decline => EventParticipationStatus.Declined,
                _ => EventParticipationStatus.Tentative,
            },
        });
        if (!string.IsNullOrEmpty(comment))
        {
            vevent.Comments = [comment];
        }

        // Every TZID the reply uses needs its VTIMEZONE (RFC 5545 §3.2.19): the stored ones come along.
        foreach (var zone in stored.TimeZones.ToList())
        {
            reply.AddChild(zone);
        }

        reply.Events.Add(vevent);
        return new CalendarSerializer().SerializeToString(reply) ?? string.Empty;
    }

    private static DateTimeOffset GraphTimeZoneInstant(DateTimeTimeZone value) =>
        Nightware.Application.Common.Time.GraphTimeZone.ToInstant(value.DateTime, value.TimeZone);

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
            foreach (var exception in calendar.Events.Where(e => e.RecurrenceIdentifier?.StartTime is not null && SameInstance(zones, e.RecurrenceIdentifier!.StartTime, occurrence)).ToList())
            {
                RemoveByReference(calendar, exception);
            }

            master.ExceptionDates.Add(InstanceValue(master, zones, occurrence));
            Touch(master);
            return Result.Success();
        });

    /// <summary>
    /// Sets the PARTSTAT of <paramref name="attendeeAddress"/> on the master and every
    /// exception (or only on <paramref name="occurrence"/>, creating its exception component
    /// when the occurrence is still unmodified). Removes RSVP
    /// and, when <paramref name="comment"/> is given, records it as the component's COMMENT.
    /// </summary>
    /// <param name="serverReplies">
    /// False marks the ORGANIZER <c>SCHEDULE-AGENT=CLIENT</c> on the edited components, which tells Sabre to send no
    /// REPLY (RFC 6638 §7.1); true clears that mark, so a later answer is sent again.
    /// </param>
    public Result<string> SetParticipation(string ics, string attendeeAddress, RsvpResponse response, DavRecurrenceId? occurrence = null, string? comment = null, bool serverReplies = true) =>
        Edit(ics, calendar =>
        {
            var zones = new IcalZoneContext(calendar, "UTC");
            List<IcalEvent> targets;
            if (occurrence is { } rid)
            {
                // An unmodified occurrence has no component of its own yet: give it one (ADR 0001 §4).
                var instance = EnsureException(calendar, zones, rid);
                if (!instance.IsSuccess)
                {
                    return instance.Error;
                }

                targets = [instance.Value];
            }
            else
            {
                targets = calendar.Events.ToList();
            }

            var partStat = response switch
            {
                RsvpResponse.Accept => EventParticipationStatus.Accepted,
                RsvpResponse.Decline => EventParticipationStatus.Declined,
                _ => EventParticipationStatus.Tentative,
            };
            var changed = false;
            foreach (var vevent in targets)
            {
                var matched = false;
                foreach (var attendee in vevent.Attendees.Where(a => string.Equals(IcalNetCalendarCodec.AddressOf(a.Value), attendeeAddress.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    attendee.ParticipationStatus = partStat;
                    attendee.Rsvp = false;
                    matched = true;
                }

                if (!matched)
                {
                    continue;
                }

                if (comment is not null)
                {
                    vevent.Comments = [comment];
                }

                if (vevent.Organizer is { } organizer)
                {
                    organizer.Parameters.Remove(ScheduleAgent);
                    if (!serverReplies)
                    {
                        organizer.Parameters.Add(ScheduleAgent, "CLIENT");
                    }
                }

                Touch(vevent);
                changed = true;
            }

            return changed ? Result.Success() : Error.NotFound("The mailbox is not an attendee of this event.");
        });

    /// <summary>
    /// An invitation (iTIP REQUEST) made storable as the attendee's copy: no METHOD (a stored object must not carry one),
    /// and the ORGANIZER marked <c>SCHEDULE-AGENT=CLIENT</c> so Sabre sends nothing for it. An RSVP later clears the
    /// mark (<see cref="SetParticipation"/>), and Sabre then sends the answer.
    /// </summary>
    public static Result<string> ForImport(string ics) =>
        Edit(ics, calendar =>
        {
            if (calendar.Events.Count == 0)
            {
                return Error.Validation("The invitation holds no event.");
            }

            calendar.Method = null;
            foreach (var vevent in calendar.Events)
            {
                if (vevent.Organizer is { } organizer)
                {
                    organizer.Parameters.Remove(ScheduleAgent);
                    organizer.Parameters.Add(ScheduleAgent, "CLIENT");
                }
            }

            return Result.Success();
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

    /// <summary>
    /// VEVENTs compare equal by UID, so <c>Events.Remove</c>/<c>Children.Remove</c> would drop the
    /// master instead of the exception; remove the exact instance by position.
    /// </summary>
    private static void RemoveByReference(IcalCalendar calendar, IcalEvent component)
    {
        if (calendar.Children is IList<ICalendarObject> children)
        {
            for (var i = 0; i < children.Count; i++)
            {
                if (ReferenceEquals(children[i], component))
                {
                    children.RemoveAt(i);
                    return;
                }
            }
        }
    }

    private static IcalEvent? Master(IcalCalendar calendar) => calendar.Events.FirstOrDefault(e => e.RecurrenceIdentifier?.StartTime is null);

    private static bool SameInstance(IcalZoneContext zones, Ical.Net.DataTypes.CalDateTime recurrenceId, DavRecurrenceId wanted) =>
        recurrenceId.HasTime
            ? !wanted.IsDate && zones.ToInstant(recurrenceId) == wanted.ToUtc()
            : wanted.IsDate && recurrenceId.Date == wanted.ToDate();
}
