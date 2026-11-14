using System.Globalization;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Nightware.Adapters.Dav.Core;
using Nightware.Application.Calendar;
using Nightware.Application.Common.Time;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using DomainAttendee = Nightware.Domain.Calendar.Attendee;
using DomainEvent = Nightware.Domain.Calendar.CalendarEvent;
using IcalAttendee = Ical.Net.DataTypes.Attendee;
using IcalCalendar = Ical.Net.Calendar;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace Nightware.Adapters.Dav.Calendar.ICal;

/// <summary>
/// <see cref="IICalendarCodec"/> on Ical.Net 5: VEVENT ↔ Domain <see cref="DomainEvent"/>.
/// <para>
/// The Domain model is lossy (no SCHEDULE-AGENT, X- properties, ATTACH, RSVP comments…), so
/// never regenerate a stored resource from Domain to apply a small change; edit the stored
/// iCalendar instead (<see cref="IcsEditor"/>).
/// </para>
/// <para>
/// EXDATEs are surfaced as cancelled exception instances (<c>Type = Exception</c>,
/// <c>IsCancelled = true</c>, <c>OriginalStart</c> = the excluded RECURRENCE-ID) and written
/// back as EXDATE on the master when the master is in the same document. A cancelled
/// exception without its master (e.g. METHOD:CANCEL of one instance) is written as a
/// VEVENT with RECURRENCE-ID and STATUS:CANCELLED.
/// </para>
/// </summary>
public sealed class IcalNetCalendarCodec : IICalendarCodec
{
    public const string ProductId = "-//Nightware//Nightware//EN";
    private const string AltDescription = "X-ALT-DESC";
    private const string BusyStatus = "X-MICROSOFT-CDO-BUSYSTATUS";
    private const string TeamsUrl = "X-MICROSOFT-SKYPETEAMSMEETINGURL";
    private const string GoogleConference = "X-GOOGLE-CONFERENCE";

    private readonly TimeProvider _timeProvider;

    public IcalNetCalendarCodec(TimeProvider timeProvider) =>
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public Result<CalendarDocument> Parse(string ics) => Parse(ics, CalendarParseOptions.Default);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Instance API of a DI-registered codec.")]
    public Result<CalendarDocument> Parse(string ics, CalendarParseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Href is not null && !DavHrefRules.IsSafeRelative(options.Href))
        {
            return Error.Validation("The resource href is not a safe DAV path.", "href");
        }

        var loaded = Load(ics);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        var calendar = loaded.Value;
        var zones = new IcalZoneContext(calendar, options.FloatingTimeZone);
        var method = string.IsNullOrWhiteSpace(calendar.Method) ? null : calendar.Method.Trim().ToUpperInvariant();
        var vevents = calendar.Events.ToList();
        var events = new List<DomainEvent>(vevents.Count);
        foreach (var vevent in vevents)
        {
            var domain = ToDomain(vevent, zones, options, method);
            events.Add(domain);
            if (vevent.RecurrenceIdentifier?.StartTime is null && vevent.ExceptionDates is not null)
            {
                events.AddRange(Tombstones(vevent, domain, vevents, zones, options));
            }
        }

        return new CalendarDocument(method, events);
    }

    public string Serialize(CalendarDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var calendar = new IcalCalendar { ProductId = ProductId };
        var method = document.Method?.Trim().ToUpperInvariant();
        if (!string.IsNullOrEmpty(method))
        {
            calendar.Method = method;
        }

        var writer = new ZoneWriter();
        var isCancel = method == CalendarMethods.Cancel;
        var groups = document.Events.GroupBy(e => e.ICalUId ?? Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture), StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var master = group.FirstOrDefault(e => e.OriginalStart is null);
            IcalEvent? masterEvent = null;
            foreach (var single in group.Where(e => e.OriginalStart is null))
            {
                var vevent = ToVevent(single, group.Key, writer, isMasterOfSeries: true);
                masterEvent ??= vevent;
                calendar.Events.Add(vevent);
            }

            foreach (var exception in group.Where(e => e.OriginalStart is not null))
            {
                var recurrenceId = RecurrenceIdValue(exception.OriginalStart!.Value, master, masterEvent, exception.IsAllDay);
                if (exception.IsCancelled && masterEvent is not null && master!.Recurrence is not null && !isCancel)
                {
                    masterEvent.ExceptionDates.Add(recurrenceId);
                    continue;
                }

                var vevent = ToVevent(exception, group.Key, writer, isMasterOfSeries: false);
                vevent.RecurrenceIdentifier = new RecurrenceIdentifier(recurrenceId, null);
                calendar.Events.Add(vevent);
            }
        }

        writer.AddTimeZones(calendar);
        return new CalendarSerializer().SerializeToString(calendar) ?? string.Empty;
    }

    internal static Result<IcalCalendar> Load(string? ics)
    {
        if (string.IsNullOrWhiteSpace(ics) || !ics.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation("The content is not an iCalendar object.");
        }

        try
        {
            return IcalCalendar.Load(ics) is { } calendar
                ? calendar
                : Error.Validation("The content is not an iCalendar object.");
        }
#pragma warning disable CA1031 // Ical.Net throws a variety of exception types for malformed input.
        catch (Exception ex) when (ex is not OutOfMemoryException)
#pragma warning restore CA1031
        {
            return Error.Validation("The iCalendar content could not be parsed.");
        }
    }

    private static DomainEvent ToDomain(IcalEvent vevent, IcalZoneContext zones, CalendarParseOptions options, string? method)
    {
        var dtStart = vevent.DtStart ?? vevent.RecurrenceIdentifier?.StartTime ?? vevent.DtStamp;
        var isAllDay = dtStart is not null && !dtStart.HasTime;
        var start = dtStart is null
            ? new DateTimeTimeZone(DateTime.UnixEpoch, GraphTimeZone.Utc)
            : zones.ToDateTimeTimeZone(dtStart);
        var end = EndOf(vevent, start, isAllDay, zones);

        var recurrenceId = vevent.RecurrenceIdentifier?.StartTime;
        var type = recurrenceId is not null
            ? options.Expanded ? EventType.Occurrence : EventType.Exception
            : vevent.RecurrenceRule is not null ? EventType.SeriesMaster : EventType.SingleInstance;
        var (id, masterId) = IdsFor(options.Href, recurrenceId, zones);

        var organizer = vevent.Organizer is { } o && AddressOf(o.Value) is { } organizerAddress
            ? new EmailAddress(organizerAddress, o.CommonName)
            : null;
        var attendees = vevent.Attendees.Select(ToAttendee).OfType<DomainAttendee>().ToList();
        var (isOrganizer, responseStatus) = Perspective(options.Mailbox, organizer, attendees);

        var description = vevent.Description;
        var html = Property(vevent, AltDescription) is { } alt
                   && string.Equals(alt.Parameters.Get("FMTTYPE"), "text/html", StringComparison.OrdinalIgnoreCase)
            ? alt.Value?.ToString()
            : null;
        var plain = description ?? (html is null ? null : HtmlText.ToPlain(html));
        var (location, locations) = Locations(vevent.Location, attendees);
        var onlineUrl = Text(vevent, TeamsUrl) ?? Text(vevent, GoogleConference) ?? vevent.Url?.OriginalString;
        var (reminderOn, reminderMinutes) = Reminder(vevent, dtStart, start, end, zones);

        return new DomainEvent(id, vevent.Summary, start, end)
        {
            ICalUId = vevent.Uid,
            Type = type,
            SeriesMasterId = masterId,
            OriginalStart = recurrenceId is null ? null : zones.ToInstant(recurrenceId),
            IsAllDay = isAllDay,
            IsCancelled = string.Equals(vevent.Status, EventStatus.Cancelled, StringComparison.OrdinalIgnoreCase)
                          || method == CalendarMethods.Cancel,
            IsOrganizer = isOrganizer,
            BodyHtml = html ?? (description is null ? null : HtmlText.FromPlain(description)),
            BodyPreview = plain is null ? null : HtmlText.Preview(plain),
            Location = location,
            Locations = locations,
            Organizer = organizer,
            Attendees = attendees,
            ResponseStatus = responseStatus,
            ShowAs = ShowAsOf(vevent),
            Recurrence = recurrenceId is null && vevent.RecurrenceRule is { } rule && dtStart is not null
                ? IcalRecurrence.ToDomain(rule, dtStart, zones)
                : null,
            IsReminderOn = reminderOn,
            ReminderMinutesBeforeStart = reminderMinutes,
            IsOnlineMeeting = !string.IsNullOrWhiteSpace(onlineUrl),
            OnlineMeetingUrl = string.IsNullOrWhiteSpace(onlineUrl) ? null : onlineUrl.Trim(),
            Categories = vevent.Categories?.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).Distinct(StringComparer.Ordinal).ToList() ?? [],
            Sensitivity = SensitivityOf(vevent.Class),
            CreatedDateTime = vevent.Created is { } created ? zones.ToInstant(created) : null,
            LastModifiedDateTime = vevent.LastModified is { } modified ? zones.ToInstant(modified) : null,
            Sequence = vevent.Sequence,
        };
    }

    private static IEnumerable<DomainEvent> Tombstones(IcalEvent master, DomainEvent domainMaster, List<IcalEvent> all, IcalZoneContext zones, CalendarParseOptions options)
    {
        var explicitExceptions = all
            .Where(e => e.RecurrenceIdentifier?.StartTime is not null && string.Equals(e.Uid, master.Uid, StringComparison.Ordinal))
            .Select(e => zones.ToInstant(e.RecurrenceIdentifier?.StartTime!))
            .ToHashSet();
        var duration = domainMaster.End.DateTime - domainMaster.Start.DateTime;
        foreach (var excluded in master.ExceptionDates.GetAllDates())
        {
            var instant = zones.ToInstant(excluded);
            if (!explicitExceptions.Add(instant))
            {
                continue;
            }

            var start = domainMaster.IsAllDay
                ? new DateTimeTimeZone(excluded.Date.ToDateTime(TimeOnly.MinValue), GraphTimeZone.Utc)
                : new DateTimeTimeZone(IcalZoneContext.ToWallClock(instant, domainMaster.Start.TimeZone), domainMaster.Start.TimeZone);
            var (id, masterId) = IdsFor(options.Href, excluded, zones);
            yield return domainMaster with
            {
                Id = id,
                Type = EventType.Exception,
                SeriesMasterId = masterId,
                OriginalStart = instant,
                Start = start,
                End = start with { DateTime = start.DateTime + duration },
                IsCancelled = true,
                Recurrence = null,
            };
        }
    }

    private static (EventId Id, EventId? MasterId) IdsFor(string? href, CalDateTime? recurrenceId, IcalZoneContext zones)
    {
        if (href is null)
        {
            return (new EventId(string.Empty), null);
        }

        var resource = DavItemId.Resource(href);
        if (recurrenceId is null)
        {
            return (resource.ToEventId(), null);
        }

        var key = recurrenceId.HasTime
            ? DavRecurrenceId.FromUtc(zones.ToInstant(recurrenceId))
            : DavRecurrenceId.FromDate(recurrenceId.Date);
        return (DavItemId.Occurrence(href, key).ToEventId(), resource.ToEventId());
    }

    private static DateTimeTimeZone EndOf(IcalEvent vevent, DateTimeTimeZone start, bool isAllDay, IcalZoneContext zones)
    {
        if (vevent.DtEnd is { } dtEnd)
        {
            var end = zones.ToDateTimeTimeZone(dtEnd);
            if (end.TimeZone == start.TimeZone || isAllDay)
            {
                return end with { TimeZone = start.TimeZone };
            }

            return start with { DateTime = IcalZoneContext.ToWallClock(zones.ToInstant(dtEnd), start.TimeZone) };
        }

        if (vevent.Duration is { } duration)
        {
            return start with { DateTime = start.DateTime + duration.ToTimeSpanUnspecified() };
        }

        return isAllDay ? start with { DateTime = start.DateTime.AddDays(1) } : start;
    }

    private static (bool IsOrganizer, ResponseStatus Status) Perspective(MailboxRef? mailbox, EmailAddress? organizer, List<DomainAttendee> attendees)
    {
        if (mailbox is not { } me)
        {
            return (false, new ResponseStatus(ResponseType.None));
        }

        var isOrganizer = organizer is null
            ? attendees.Count == 0
            : SameAddress(organizer.Address, me.Address);
        if (isOrganizer)
        {
            return (true, new ResponseStatus(ResponseType.Organizer));
        }

        var own = attendees.FirstOrDefault(a => SameAddress(a.EmailAddress.Address, me.Address));
        return (false, own?.Status ?? new ResponseStatus(ResponseType.None));
    }

    private static DomainAttendee? ToAttendee(IcalAttendee attendee)
    {
        var address = AddressOf(attendee.Value);
        if (address is null)
        {
            return null;
        }

        var cuType = attendee.Type?.ToUpperInvariant();
        var role = attendee.Role?.ToUpperInvariant();
        var type = cuType is "ROOM" or "RESOURCE"
            ? AttendeeType.Resource
            : role is "OPT-PARTICIPANT" or "NON-PARTICIPANT" ? AttendeeType.Optional : AttendeeType.Required;
        var response = attendee.ParticipationStatus?.ToUpperInvariant() switch
        {
            "ACCEPTED" => ResponseType.Accepted,
            "DECLINED" => ResponseType.Declined,
            "TENTATIVE" => ResponseType.TentativelyAccepted,
            "NEEDS-ACTION" or null => ResponseType.NotResponded,
            _ => ResponseType.None,
        };
        return new DomainAttendee(new EmailAddress(address, attendee.CommonName), type, new ResponseStatus(response));
    }

    private static (Location? Location, IReadOnlyList<Location> Locations) Locations(string? text, List<DomainAttendee> attendees)
    {
        var rooms = attendees.Where(a => a.Type == AttendeeType.Resource).ToList();
        Location? location = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            var room = rooms.FirstOrDefault(r => string.Equals(r.EmailAddress.Name, text.Trim(), StringComparison.OrdinalIgnoreCase))
                       ?? (rooms.Count == 1 ? rooms[0] : null);
            location = new Location(text.Trim()) { LocationEmailAddress = room?.EmailAddress.Address };
        }

        var others = rooms
            .Where(r => location?.LocationEmailAddress is null || !SameAddress(r.EmailAddress.Address, location.LocationEmailAddress))
            .Select(r => new Location(r.EmailAddress.Name ?? r.EmailAddress.Address) { LocationEmailAddress = r.EmailAddress.Address });
        return (location, (location is null ? others : others.Prepend(location)).ToList());
    }

    private static (bool On, int Minutes) Reminder(IcalEvent vevent, CalDateTime? dtStart, DateTimeTimeZone start, DateTimeTimeZone end, IcalZoneContext zones)
    {
        var alarm = vevent.Alarms.FirstOrDefault(a => a.Trigger is not null && !string.Equals(a.Action, "NONE", StringComparison.OrdinalIgnoreCase));
        if (alarm?.Trigger is not { } trigger)
        {
            return (false, 0);
        }

        double minutes;
        if (trigger.Duration is { } offset)
        {
            minutes = -offset.ToTimeSpanUnspecified().TotalMinutes;
            if (string.Equals(trigger.Related, TriggerRelation.End, StringComparison.OrdinalIgnoreCase))
            {
                minutes -= (end.DateTime - start.DateTime).TotalMinutes;
            }
        }
        else if (trigger.DateTime is { } at && dtStart is not null)
        {
            minutes = (zones.ToInstant(dtStart) - zones.ToInstant(at)).TotalMinutes;
        }
        else
        {
            return (false, 0);
        }

        return (true, (int)Math.Max(0, Math.Round(minutes)));
    }

    private static ShowAs ShowAsOf(IcalEvent vevent)
    {
        var busy = Text(vevent, BusyStatus)?.Trim().ToUpperInvariant();
        switch (busy)
        {
            case "FREE": return ShowAs.Free;
            case "TENTATIVE": return ShowAs.Tentative;
            case "BUSY": return ShowAs.Busy;
            case "OOF": return ShowAs.Oof;
            case "WORKINGELSEWHERE": return ShowAs.WorkingElsewhere;
            default: break;
        }

        if (string.Equals(vevent.Transparency, TransparencyType.Transparent, StringComparison.OrdinalIgnoreCase))
        {
            return ShowAs.Free;
        }

        return string.Equals(vevent.Status, EventStatus.Tentative, StringComparison.OrdinalIgnoreCase) ? ShowAs.Tentative : ShowAs.Busy;
    }

    private static string? SensitivityOf(string? @class) => @class?.Trim().ToUpperInvariant() switch
    {
        "PUBLIC" => "normal",
        "PRIVATE" => "private",
        "CONFIDENTIAL" => "confidential",
        _ => null,
    };

    private IcalEvent ToVevent(DomainEvent source, string uid, ZoneWriter zones, bool isMasterOfSeries)
    {
        var vevent = new IcalEvent
        {
            Uid = uid,
            Summary = source.Subject,
            Sequence = Math.Max(0, source.Sequence),
            DtStamp = Utc(_timeProvider.GetUtcNow()),
            DtStart = zones.ToCalDateTime(source.Start, source.IsAllDay),
        };

        var end = source.End;
        if (source.IsAllDay && DateOnly.FromDateTime(end.DateTime) <= DateOnly.FromDateTime(source.Start.DateTime))
        {
            end = source.Start with { DateTime = source.Start.DateTime.Date.AddDays(1) };
        }

        vevent.DtEnd = zones.ToCalDateTime(end, source.IsAllDay);

        var locationText = source.Location?.DisplayName
                           ?? (source.Locations.Count > 0 ? string.Join("; ", source.Locations.Select(l => l.DisplayName)) : null);
        if (!string.IsNullOrWhiteSpace(locationText))
        {
            vevent.Location = locationText;
        }

        var plain = source.BodyHtml is { Length: > 0 } bodyHtml ? HtmlText.ToPlain(bodyHtml) : source.BodyPreview;
        if (!string.IsNullOrEmpty(plain))
        {
            vevent.Description = plain;
        }

        if (!string.IsNullOrWhiteSpace(source.BodyHtml))
        {
            var alt = new CalendarProperty(AltDescription, source.BodyHtml);
            alt.Parameters.Add("FMTTYPE", "text/html");
            vevent.AddProperty(alt);
        }

        if (source.Organizer is { } organizer)
        {
            vevent.Organizer = new Organizer(MailTo(organizer.Address)) { CommonName = organizer.Name };
        }

        var roomAddresses = source.Locations.Append(source.Location)
            .Select(l => l?.LocationEmailAddress)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var attendee in source.Attendees)
        {
            vevent.Attendees.Add(ToIcalAttendee(attendee, roomAddresses));
        }

        vevent.Transparency = source.ShowAs == ShowAs.Free ? TransparencyType.Transparent : TransparencyType.Opaque;
        if (source.ShowAs != ShowAs.Unknown)
        {
            vevent.AddProperty(BusyStatus, source.ShowAs switch
            {
                ShowAs.Free => "FREE",
                ShowAs.Tentative => "TENTATIVE",
                ShowAs.Oof => "OOF",
                ShowAs.WorkingElsewhere => "WORKINGELSEWHERE",
                _ => "BUSY",
            });
        }

        if (source.IsCancelled)
        {
            vevent.Status = EventStatus.Cancelled;
        }

        if (isMasterOfSeries && source.Recurrence is { } recurrence)
        {
            var zoneName = vevent.DtStart.IsUtc || !vevent.DtStart.HasTime ? GraphTimeZone.Utc : vevent.DtStart.TzId;
            var startTime = TimeOnly.FromDateTime(vevent.DtStart.Value);
            vevent.RecurrenceRule = IcalRecurrence.ToIcal(recurrence, source.IsAllDay, startTime, zoneName);
        }

        if (source.IsReminderOn)
        {
            vevent.Alarms.Add(new Alarm
            {
                Action = AlarmAction.Display,
                Description = "Reminder",
                Trigger = new Trigger(Duration.FromMinutes(-Math.Max(0, source.ReminderMinutesBeforeStart))),
            });
        }

        if (source.Categories.Count > 0)
        {
            vevent.Categories = source.Categories.ToList();
        }

        var @class = source.Sensitivity?.ToUpperInvariant() switch
        {
            "NORMAL" => "PUBLIC",
            "PRIVATE" or "PERSONAL" => "PRIVATE",
            "CONFIDENTIAL" => "CONFIDENTIAL",
            _ => null,
        };
        if (@class is not null)
        {
            vevent.Class = @class;
        }

        if (source.OnlineMeetingUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var meetingUri))
        {
            vevent.Url = meetingUri;
        }

        if (source.CreatedDateTime is { } createdAt)
        {
            vevent.Created = Utc(createdAt);
        }

        if (source.LastModifiedDateTime is { } modifiedAt)
        {
            vevent.LastModified = Utc(modifiedAt);
        }

        return vevent;
    }

    private static IcalAttendee ToIcalAttendee(DomainAttendee attendee, HashSet<string> roomAddresses)
    {
        var partStat = attendee.Status.Response switch
        {
            ResponseType.Accepted or ResponseType.Organizer => EventParticipationStatus.Accepted,
            ResponseType.Declined => EventParticipationStatus.Declined,
            ResponseType.TentativelyAccepted => EventParticipationStatus.Tentative,
            _ => EventParticipationStatus.NeedsAction,
        };
        var (role, cuType) = attendee.Type switch
        {
            AttendeeType.Optional => (ParticipationRole.OptionalParticipant, "INDIVIDUAL"),
            AttendeeType.Resource => (ParticipationRole.NonParticipant, roomAddresses.Contains(attendee.EmailAddress.Address) ? "ROOM" : "RESOURCE"),
            _ => (ParticipationRole.RequiredParticipant, "INDIVIDUAL"),
        };
        return new IcalAttendee(MailTo(attendee.EmailAddress.Address))
        {
            CommonName = attendee.EmailAddress.Name,
            Role = role,
            Type = cuType,
            ParticipationStatus = partStat,
            Rsvp = partStat == EventParticipationStatus.NeedsAction,
        };
    }

    private static CalDateTime RecurrenceIdValue(DateTimeOffset originalStart, DomainEvent? master, IcalEvent? masterEvent, bool isAllDay)
    {
        if (master?.IsAllDay ?? isAllDay)
        {
            return new CalDateTime(DateOnly.FromDateTime(originalStart.UtcDateTime));
        }

        var masterStart = masterEvent?.DtStart;
        if (masterStart is { HasTime: true, IsUtc: false, IsFloating: false })
        {
            return new CalDateTime(IcalZoneContext.ToWallClock(originalStart, masterStart.TzId), masterStart.TzId, true);
        }

        return Utc(originalStart);
    }

    internal static CalDateTime Utc(DateTimeOffset instant) =>
        new(DateTime.SpecifyKind(instant.UtcDateTime, DateTimeKind.Unspecified), CalDateTime.UtcTzId, true);

    internal static string MailTo(string address) => "mailto:" + address.Trim();

    internal static string? AddressOf(Uri? uri)
    {
        if (uri is null)
        {
            return null;
        }

        var text = uri.OriginalString.Trim();
        if (text.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            text = text["mailto:".Length..];
        }

        text = Uri.UnescapeDataString(text).Trim();
        return text.Contains('@', StringComparison.Ordinal) ? text : null;
    }

    private static bool SameAddress(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    internal static CalendarProperty? Property(CalendarComponent component, string name) =>
        component.Properties.OfType<CalendarProperty>().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    internal static string? Text(CalendarComponent component, string name) =>
        Property(component, name)?.Value?.ToString() is { Length: > 0 } value ? value : null;
}
