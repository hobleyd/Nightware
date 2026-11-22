using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Nightware.Adapters.Dav.Core;
using Nightware.Application.Common.Time;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using DomainAttendee = Nightware.Domain.Calendar.Attendee;
using IcalAttendee = Ical.Net.DataTypes.Attendee;
using IcalCalendar = Ical.Net.Calendar;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace Nightware.Adapters.Dav.Calendar.ICal;

// Graph PATCH and forward applied to the stored resource, property by property, so whatever the Domain
// model cannot carry survives the edit (README: "Never regenerate a stored event from Domain").
public sealed partial class IcsEditor
{
    private const string AltDescription = "X-ALT-DESC";
    private const string BusyStatus = "X-MICROSOFT-CDO-BUSYSTATUS";
    private const string ScheduleForceSend = "SCHEDULE-FORCE-SEND";

    /// <summary>
    /// Applies a Graph PATCH to the series (or, for <paramref name="occurrence"/>, to that occurrence only,
    /// creating its exception component). Null members of <paramref name="patch"/> are left alone.
    /// <list type="bullet">
    /// <item>Attendees are merged by address: one who stays keeps their ATTENDEE property (PARTSTAT,
    /// SCHEDULE-STATUS, CN…), a new one is NEEDS-ACTION with RSVP, and the organizer's own line is never
    /// dropped. A series edit applies the list to the modified occurrences too.</item>
    /// <item>When <paramref name="organizerAddress"/> organizes the meeting and the schedule really changes
    /// (NightMail resends start and end on every edit), SEQUENCE is bumped so Sabre re-sends the REQUEST
    /// (ADR 0001 §4).</item>
    /// <item>When the patch gives the organizer's meeting its first join URL (phase 22), the attendees are sent the
    /// update: SEQUENCE is bumped and, on the main component, each person attendee gets RFC 6638
    /// <c>SCHEDULE-FORCE-SEND=REQUEST</c> (Nextcloud does not count <c>URL</c> as a significant change, and strips
    /// the parameter from that component once it has sent).</item>
    /// </list>
    /// </summary>
    /// <param name="organizerAddress">The mailbox making the change; it becomes the ORGANIZER of an event that gains its first attendees.</param>
    public Result<string> ApplyPatch(string ics, EventPatch patch, DavRecurrenceId? occurrence, string organizerAddress)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentException.ThrowIfNullOrWhiteSpace(organizerAddress);
        if (occurrence is not null && (patch.Recurrence is not null || patch.ClearRecurrence))
        {
            return Error.Validation("An occurrence cannot have a recurrence of its own.", "recurrence");
        }

        return Edit(ics, calendar =>
        {
            var zones = new IcalZoneContext(calendar, "UTC");
            var target = occurrence is { } rid ? EnsureException(calendar, zones, rid) : Primary(calendar);
            if (!target.IsSuccess)
            {
                return target.Error;
            }

            var vevent = target.Value;
            var rescheduled = ApplyTimes(calendar, vevent, zones, patch);
            if (patch.Recurrence is { } recurrence)
            {
                rescheduled |= ApplyRecurrence(vevent, recurrence);
            }
            else if (patch.ClearRecurrence)
            {
                rescheduled |= ClearRecurrence(calendar, vevent);
            }

            var urlBefore = vevent.Url;
            ApplyDetails(vevent, patch);
            var linked = vevent.Url is not null && vevent.Url != urlBefore;
            if (patch.Attendees is { } attendees)
            {
                // The attendee list is the meeting's: a series edit also reaches the modified occurrences,
                // or a removed attendee would stay invited to them.
                foreach (var component in occurrence is null ? calendar.Events.ToList() : [vevent])
                {
                    MergeAttendees(component, attendees, patch.Location, patch.Locations, organizerAddress);
                }
            }

            if (linked && IsOrganizedBy(vevent, organizerAddress))
            {
                rescheduled = true;
                if (ReferenceEquals(vevent, calendar.Events.FirstOrDefault()))
                {
                    ForceSend(vevent, organizerAddress);
                }
            }

            var bumpAll = occurrence is null && rescheduled;
            foreach (var component in bumpAll ? calendar.Events.ToList() : [vevent])
            {
                if (rescheduled && IsOrganizedBy(component, organizerAddress))
                {
                    component.Sequence++;
                }

                Touch(component);
            }

            return Result.Success();
        });
    }

    /// <summary>
    /// Forward by the organizer: adds the recipients as required attendees (NEEDS-ACTION, RSVP) to the series
    /// and its exceptions, or to <paramref name="occurrence"/> only, so Sabre invites them. Recipients who are
    /// already attendees are left as they are.
    /// </summary>
    public Result<string> AddAttendees(string ics, IReadOnlyList<EmailAddress> recipients, DavRecurrenceId? occurrence = null)
    {
        ArgumentNullException.ThrowIfNull(recipients);
        return Edit(ics, calendar =>
        {
            List<IcalEvent> targets;
            if (occurrence is { } rid)
            {
                var instance = EnsureException(calendar, new IcalZoneContext(calendar, "UTC"), rid);
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

            foreach (var vevent in targets)
            {
                var present = vevent.Attendees.Select(a => IcalNetCalendarCodec.AddressOf(a.Value)).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
                var added = recipients.Where(r => present.Add(r.Address.Trim())).ToList();
                foreach (var recipient in added)
                {
                    vevent.Attendees.Add(NewAttendee(new DomainAttendee(recipient, AttendeeType.Required, new ResponseStatus(ResponseType.None)), isRoom: false));
                }

                if (added.Count > 0)
                {
                    Touch(vevent);
                }
            }

            return Result.Success();
        });
    }

    /// <summary>The series master, or for a resource holding only exceptions (invited to one occurrence), the first.</summary>
    private static Result<IcalEvent> Primary(IcalCalendar calendar)
    {
        var vevent = Master(calendar) ?? calendar.Events.FirstOrDefault();
        return vevent is null ? Error.NotFound("The resource holds no event.") : vevent;
    }

    /// <summary>Applies start, end and all-day; true when the event's timing actually changed.</summary>
    private static bool ApplyTimes(IcalCalendar calendar, IcalEvent vevent, IcalZoneContext zones, EventPatch patch)
    {
        if ((patch.Start is null && patch.End is null && patch.IsAllDay is null) || vevent.DtStart is null)
        {
            return false;
        }

        var wasAllDay = !vevent.DtStart.HasTime;
        var allDay = patch.IsAllDay ?? wasAllDay;
        var oldStart = zones.ToDateTimeTimeZone(vevent.DtStart);
        var oldEnd = vevent.DtEnd is { } dtEnd
            ? zones.ToDateTimeTimeZone(dtEnd)
            : oldStart with { DateTime = oldStart.DateTime + (vevent.Duration?.ToTimeSpanUnspecified() ?? (wasAllDay ? TimeSpan.FromDays(1) : TimeSpan.Zero)) };
        var start = patch.Start ?? oldStart;
        var end = patch.End ?? (patch.Start is null ? oldEnd : start with { DateTime = start.DateTime + (oldEnd.DateTime - oldStart.DateTime) });

        var newStart = ToValue(calendar, start, allDay);
        var newEnd = ToValue(calendar, end, allDay);
        if (allDay && newEnd.Date <= newStart.Date)
        {
            newEnd = newStart.AddDays(1);
        }

        var changed = allDay != wasAllDay
                      || zones.ToInstant(newStart) != zones.ToInstant(vevent.DtStart)
                      || vevent.DtEnd is null
                      || zones.ToInstant(newEnd) != zones.ToInstant(vevent.DtEnd);
        // An unchanged time stays as stored: NightMail resends it in the device's zone, and rewriting a
        // series into another zone would change how it follows daylight saving.
        if (changed)
        {
            vevent.Properties.Remove("DURATION");
            vevent.DtStart = newStart;
            vevent.DtEnd = newEnd;
        }

        return changed;
    }

    private static bool ApplyRecurrence(IcalEvent vevent, PatternedRecurrence recurrence)
    {
        if (vevent.DtStart is not { } start)
        {
            return false;
        }

        var zone = start.IsUtc || !start.HasTime ? GraphTimeZone.Utc : start.TzId;
        var rule = IcalRecurrence.ToIcal(recurrence, !start.HasTime, TimeOnly.FromDateTime(start.Value), zone);
        if (vevent.RecurrenceRule is { } current && string.Equals(current.ToString(), rule.ToString(), StringComparison.Ordinal))
        {
            return false;
        }

        vevent.RecurrenceRule = rule;
        return true;
    }

    /// <summary>
    /// Makes the series a single event: the master keeps its DTSTART (the first occurrence) and loses RRULE, RDATE
    /// and EXDATE, and every exception component goes. False when it was not recurring.
    /// </summary>
    private static bool ClearRecurrence(IcalCalendar calendar, IcalEvent master)
    {
        var exceptions = calendar.Events.Where(e => !ReferenceEquals(e, master) && e.RecurrenceIdentifier?.StartTime is not null).ToList();
        var recurring = master.RecurrenceRule is not null || master.RecurrenceDates.GetAllDates().Any() || master.ExceptionDates.GetAllDates().Any() || exceptions.Count > 0;
        master.RecurrenceRule = null;
        master.Properties.Remove("RDATE");
        master.Properties.Remove("EXDATE");
        foreach (var exception in exceptions)
        {
            RemoveByReference(calendar, exception);
        }

        return recurring;
    }

    private static void ApplyDetails(IcalEvent vevent, EventPatch patch)
    {
        if (patch.Subject is { } subject)
        {
            vevent.Summary = subject;
        }

        if (patch.BodyHtml is { } html)
        {
            vevent.Properties.Remove(AltDescription);
            if (html.Length == 0)
            {
                vevent.Description = null;
            }
            else
            {
                vevent.Description = HtmlText.ToPlain(html);
                var alt = new CalendarProperty(AltDescription, html);
                alt.Parameters.Add("FMTTYPE", "text/html");
                vevent.AddProperty(alt);
            }
        }

        if (patch.Location is { } location)
        {
            vevent.Location = string.IsNullOrWhiteSpace(location.DisplayName) ? null : location.DisplayName.Trim();
        }

        if (patch.ShowAs is { } showAs)
        {
            vevent.Transparency = showAs == ShowAs.Free ? TransparencyType.Transparent : TransparencyType.Opaque;
            vevent.Properties.Remove(BusyStatus);
            if (showAs != ShowAs.Unknown)
            {
                vevent.AddProperty(BusyStatus, showAs switch
                {
                    ShowAs.Free => "FREE",
                    ShowAs.Tentative => "TENTATIVE",
                    ShowAs.Oof => "OOF",
                    ShowAs.WorkingElsewhere => "WORKINGELSEWHERE",
                    _ => "BUSY",
                });
            }
        }

        ApplyReminder(vevent, patch.IsReminderOn, patch.ReminderMinutesBeforeStart);

        if (patch.Categories is { } categories)
        {
            vevent.Categories = categories.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).Distinct(StringComparer.Ordinal).ToList();
        }

        if (patch.OnlineMeetingUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var meetingUri))
        {
            vevent.Url = meetingUri;
        }
    }

    /// <summary>Graph has one reminder: it maps to the first VALARM; further alarms are left alone unless reminders are turned off.</summary>
    private static void ApplyReminder(IcalEvent vevent, bool? isOn, int? minutes)
    {
        if (isOn == false)
        {
            vevent.Alarms.Clear();
            return;
        }

        if (isOn is null && minutes is null)
        {
            return;
        }

        var trigger = new Trigger(Duration.FromMinutes(-Math.Max(0, minutes ?? 15)));
        var alarm = vevent.Alarms.FirstOrDefault(a => a.Trigger is not null && !string.Equals(a.Action, "NONE", StringComparison.OrdinalIgnoreCase));
        if (alarm is null)
        {
            vevent.Alarms.Add(new Alarm { Action = AlarmAction.Display, Description = "Reminder", Trigger = trigger });
        }
        else if (minutes is not null)
        {
            alarm.Trigger = trigger;
        }
    }

    private static void MergeAttendees(IcalEvent vevent, IReadOnlyList<DomainAttendee> wanted, Location? location, IReadOnlyList<Location>? locations, string organizerAddress)
    {
        var organizer = IcalNetCalendarCodec.AddressOf(vevent.Organizer?.Value);
        var rooms = (locations ?? []).Append(location)
            .Select(l => l?.LocationEmailAddress)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byAddress = wanted
            .GroupBy(a => a.EmailAddress.Address.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var kept = new List<IcalAttendee>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attendee in vevent.Attendees)
        {
            var address = IcalNetCalendarCodec.AddressOf(attendee.Value);
            if (address is null)
            {
                kept.Add(attendee);
                continue;
            }

            if (byAddress.TryGetValue(address, out var update))
            {
                Reconcile(attendee, update, rooms.Contains(address));
                kept.Add(attendee);
                seen.Add(address);
            }
            else if (organizer is not null && string.Equals(address, organizer, StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(attendee);
                seen.Add(address);
            }
        }

        kept.AddRange(byAddress.Where(p => seen.Add(p.Key)).Select(p => NewAttendee(p.Value, rooms.Contains(p.Key))));
        vevent.Attendees.Clear();
        foreach (var attendee in kept)
        {
            vevent.Attendees.Add(attendee);
        }

        if (vevent.Organizer is null && kept.Count > 0)
        {
            // An appointment that gains attendees becomes a meeting: Sabre schedules only with an ORGANIZER.
            vevent.Organizer = new Organizer(IcalNetCalendarCodec.MailTo(organizerAddress));
        }
    }

    /// <summary>Keeps the attendee's response and scheduling state; only the role, type and missing name follow the request.</summary>
    private static void Reconcile(IcalAttendee attendee, DomainAttendee wanted, bool isRoom)
    {
        var (role, cuType) = RoleOf(wanted.Type, isRoom);
        var currentType = attendee.Type?.ToUpperInvariant();
        var wasResource = currentType is "ROOM" or "RESOURCE";
        if (wasResource != (wanted.Type == AttendeeType.Resource))
        {
            attendee.Type = cuType;
            attendee.Role = role;
        }
        else if (wanted.Type != AttendeeType.Resource && !string.Equals(attendee.Role, role, StringComparison.OrdinalIgnoreCase)
                 && !string.Equals(attendee.Role, "CHAIR", StringComparison.OrdinalIgnoreCase))
        {
            attendee.Role = role;
        }

        if (string.IsNullOrWhiteSpace(attendee.CommonName) && !string.IsNullOrWhiteSpace(wanted.EmailAddress.Name))
        {
            attendee.CommonName = wanted.EmailAddress.Name;
        }
    }

    private static IcalAttendee NewAttendee(DomainAttendee attendee, bool isRoom)
    {
        var (role, cuType) = RoleOf(attendee.Type, isRoom);
        return new IcalAttendee(IcalNetCalendarCodec.MailTo(attendee.EmailAddress.Address))
        {
            CommonName = string.IsNullOrWhiteSpace(attendee.EmailAddress.Name) ? null : attendee.EmailAddress.Name,
            Role = role,
            Type = cuType,
            ParticipationStatus = EventParticipationStatus.NeedsAction,
            Rsvp = true,
        };
    }

    /// <summary>Same mapping as the codec: rooms (matched by a location address) are CUTYPE=ROOM, other resources RESOURCE.</summary>
    private static (string Role, string CuType) RoleOf(AttendeeType type, bool isRoom) => type switch
    {
        AttendeeType.Optional => (ParticipationRole.OptionalParticipant, "INDIVIDUAL"),
        AttendeeType.Resource => (ParticipationRole.NonParticipant, isRoom ? "ROOM" : "RESOURCE"),
        _ => (ParticipationRole.RequiredParticipant, "INDIVIDUAL"),
    };

    /// <summary>Asks the server to send every person attendee the update, significant or not (RFC 6638 §7.3).</summary>
    /// <remarks>The organizer's own line is left alone: it gets no message, so the parameter would stay stored.</remarks>
    private static void ForceSend(IcalEvent vevent, string organizerAddress)
    {
        foreach (var attendee in vevent.Attendees.Where(a => a.Type?.ToUpperInvariant() is not ("ROOM" or "RESOURCE")
                                                             && !string.Equals(IcalNetCalendarCodec.AddressOf(a.Value), organizerAddress.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            attendee.Parameters.Remove(ScheduleForceSend);
            attendee.Parameters.Add(ScheduleForceSend, "REQUEST");
        }
    }

    private static bool IsOrganizedBy(IcalEvent vevent, string address) =>
        vevent.Attendees.Count > 0
        && IcalNetCalendarCodec.AddressOf(vevent.Organizer?.Value) is { } organizer
        && string.Equals(organizer, address.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A Graph wall-clock time as an iCalendar value: a date when all-day, the zone's IANA TZID (with a
    /// VTIMEZONE added once) when it is known to the tz database, UTC otherwise.
    /// </summary>
    private static CalDateTime ToValue(IcalCalendar calendar, DateTimeTimeZone value, bool allDay)
    {
        if (allDay)
        {
            return new CalDateTime(DateOnly.FromDateTime(value.DateTime));
        }

        var wall = DateTime.SpecifyKind(value.DateTime, DateTimeKind.Unspecified);
        if (ZoneWriter.IanaOf(value.TimeZone) is not { } iana)
        {
            return IcalNetCalendarCodec.Utc(GraphTimeZone.ToInstant(wall, value.TimeZone));
        }

        if (!calendar.TimeZones.Any(tz => string.Equals(tz.TzId, iana, StringComparison.Ordinal)))
        {
            calendar.AddTimeZone(iana, ZoneWriter.SupportedFrom(wall), false);
        }

        return new CalDateTime(wall, iana, true);
    }
}
