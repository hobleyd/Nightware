using Nightware.Api.Graph;
using Nightware.Application.Common.Time;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Features.Calendar.Dto;

/// <summary>
/// Maps Domain calendar types to the Graph DTOs. Times are converted to the <c>Prefer: outlook.timezone</c>
/// zone only when the request sent one; otherwise they keep the zone the event is stored in (Graph answers
/// a create in the zone it was given, which NightMail relies on).
/// </summary>
public static class EventDtoMapper
{
    /// <summary>What Graph writes for an unknown response time.</summary>
    public static readonly DateTimeOffset UnknownTime = new(1, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string DefaultIndex = "first";

    public static EventDto ToDto(CalendarEvent calendarEvent, PreferHeader? prefer = null)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        var e = calendarEvent;
        return new EventDto
        {
            Id = e.Id.Value,
            ICalUId = e.ICalUId,
            Subject = e.Subject,
            BodyPreview = e.BodyPreview ?? string.Empty,
            IsAllDay = e.IsAllDay,
            ShowAs = e.ShowAs,
            IsOrganizer = e.IsOrganizer,
            IsReminderOn = e.IsReminderOn,
            ReminderMinutesBeforeStart = e.ReminderMinutesBeforeStart,
            SeriesMasterId = e.SeriesMasterId?.Value,
            ResponseStatus = ToDto(e.ResponseStatus),
            Start = ToDto(e.Start, e.IsAllDay, prefer),
            End = ToDto(e.End, e.IsAllDay, prefer),
            Location = ToDto(e.Location),
            Recurrence = e.Recurrence is null ? null : ToDto(e.Recurrence, e.Start.TimeZone),
            Attendees = e.Attendees.Select(ToDto).ToList(),
            Organizer = e.Organizer is null ? null : new RecipientDto { EmailAddress = ToDto(e.Organizer) },
            OnlineMeeting = string.IsNullOrWhiteSpace(e.OnlineMeetingUrl) ? null : new OnlineMeetingDto { JoinUrl = e.OnlineMeetingUrl },
        };
    }

    /// <summary>
    /// A wall-clock time in the preferred zone. All-day values are dates, not instants: Graph keeps the
    /// midnight and only relabels the zone, so a holiday does not drift to the previous day.
    /// </summary>
    public static DateTimeTimeZoneDto ToDto(DateTimeTimeZone value, bool isAllDay, PreferHeader? prefer)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (prefer?.TimeZoneName is null)
        {
            return new DateTimeTimeZoneDto { DateTime = value.DateTime, TimeZone = value.TimeZone };
        }

        var wallClock = isAllDay
            ? value.DateTime
            : GraphTimeZone.ToWallClock(GraphTimeZone.ToInstant(value.DateTime, value.TimeZone), prefer.TimeZone);
        return new DateTimeTimeZoneDto { DateTime = wallClock, TimeZone = prefer.TimeZoneLabel };
    }

    /// <summary>An instant as a wall-clock time in <paramref name="zone"/>, labelled <paramref name="label"/>.</summary>
    public static DateTimeTimeZoneDto ToDto(DateTimeOffset instant, TimeZoneInfo zone, string label) =>
        new() { DateTime = GraphTimeZone.ToWallClock(instant, zone), TimeZone = label };

    public static EmailAddressDto ToDto(EmailAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        // Graph fills a missing display name with the address.
        return new EmailAddressDto { Name = string.IsNullOrWhiteSpace(address.Name) ? address.Address : address.Name, Address = address.Address };
    }

    private static ResponseStatusDto ToDto(ResponseStatus status) =>
        new() { Response = status.Response, Time = status.Time ?? UnknownTime };

    private static AttendeeDto ToDto(Attendee attendee) =>
        new() { Type = attendee.Type, Status = ToDto(attendee.Status), EmailAddress = ToDto(attendee.EmailAddress) };

    private static LocationDto ToDto(Location? location)
    {
        if (location is null)
        {
            return new LocationDto { DisplayName = string.Empty };
        }

        var isRoom = !string.IsNullOrWhiteSpace(location.LocationEmailAddress);
        return new LocationDto
        {
            DisplayName = location.DisplayName,
            LocationType = isRoom ? LocationDto.ConferenceRoom : null,
            LocationEmailAddress = isRoom ? location.LocationEmailAddress : null,
        };
    }

    private static PatternedRecurrenceDto ToDto(PatternedRecurrence recurrence, string seriesZone)
    {
        var pattern = recurrence.Pattern;
        var range = recurrence.Range;
        var zone = range.RecurrenceTimeZone ?? seriesZone;
        return new PatternedRecurrenceDto
        {
            Pattern = new RecurrencePatternDto
            {
                Type = pattern.Type,
                Interval = pattern.Interval,
                Month = pattern.Month,
                DayOfMonth = pattern.DayOfMonth,
                DaysOfWeek = pattern.DaysOfWeek,
                FirstDayOfWeek = pattern.FirstDayOfWeek,
                Index = pattern.Index ?? DefaultIndex,
            },
            Range = new RecurrenceRangeDto
            {
                Type = range.Type,
                StartDate = range.StartDate,
                EndDate = range.Type == RecurrenceRangeType.EndDate && range.EndDate is { } endDate ? endDate : DateOnly.MinValue,
                RecurrenceTimeZone = string.IsNullOrWhiteSpace(zone) ? null : GraphTimeZone.ToWindowsId(zone),
                NumberOfOccurrences = range.Type == RecurrenceRangeType.Numbered ? range.NumberOfOccurrences : 0,
            },
        };
    }
}
