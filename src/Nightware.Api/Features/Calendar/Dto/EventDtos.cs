using System.Text.Json.Serialization;
using Nightware.Domain.Calendar;

namespace Nightware.Api.Features.Calendar.Dto;

// The Graph event DTO family. Members are what NightMail reads or sends (graph_api_datasource_impl.dart,
// calendar_event_model.dart); the default projection matches the conformance fixtures. Property names
// are camelCased and enums written as Graph's camelCase strings by GraphJson. These are the calendar's
// own types: the frozen mail DTOs carry fewer members (e.g. location has only displayName there).

/// <summary>Graph <c>dateTimeTimeZone</c>: wall clock (seven fractional digits, no <c>Z</c>) plus the zone label.</summary>
public sealed record DateTimeTimeZoneDto
{
    public DateTime DateTime { get; init; }

    public string? TimeZone { get; init; }
}

/// <summary>Graph <c>emailAddress</c>.</summary>
public sealed record EmailAddressDto
{
    public string? Name { get; init; }

    public string? Address { get; init; }
}

/// <summary>Graph <c>recipient</c>.</summary>
public sealed record RecipientDto
{
    public EmailAddressDto? EmailAddress { get; init; }
}

/// <summary>Graph <c>responseStatus</c>. Graph writes <c>0001-01-01T00:00:00Z</c> when the time is unknown.</summary>
public sealed record ResponseStatusDto
{
    public ResponseType Response { get; init; }

    public DateTimeOffset Time { get; init; }
}

/// <summary>Graph <c>attendee</c>.</summary>
public sealed record AttendeeDto
{
    public AttendeeType Type { get; init; }

    public ResponseStatusDto? Status { get; init; }

    public EmailAddressDto? EmailAddress { get; init; }
}

/// <summary>Graph <c>location</c>. A room carries its address and <c>conferenceRoom</c>; otherwise only the name.</summary>
public sealed record LocationDto
{
    public const string ConferenceRoom = "conferenceRoom";

    public string? DisplayName { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LocationType { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LocationEmailAddress { get; init; }
}

/// <summary>Graph <c>onlineMeetingInfo</c>; the client reads only <c>joinUrl</c>.</summary>
public sealed record OnlineMeetingDto
{
    public string? JoinUrl { get; init; }
}

/// <summary>Graph <c>recurrencePattern</c>. Graph always writes every member, defaulted when unused.</summary>
public sealed record RecurrencePatternDto
{
    public RecurrencePatternType Type { get; init; }

    public int Interval { get; init; }

    public int Month { get; init; }

    public int DayOfMonth { get; init; }

    public IReadOnlyList<DayOfWeek>? DaysOfWeek { get; init; }

    public DayOfWeek FirstDayOfWeek { get; init; }

    /// <summary><c>first</c>, <c>second</c>, <c>third</c>, <c>fourth</c> or <c>last</c>.</summary>
    public string? Index { get; init; }
}

/// <summary>Graph <c>recurrenceRange</c>. <c>endDate</c> is <c>0001-01-01</c> unless the range ends on a date.</summary>
public sealed record RecurrenceRangeDto
{
    public RecurrenceRangeType Type { get; init; }

    public DateOnly StartDate { get; init; }

    public DateOnly EndDate { get; init; }

    /// <summary>A Windows zone name, as Graph writes it.</summary>
    public string? RecurrenceTimeZone { get; init; }

    public int NumberOfOccurrences { get; init; }
}

/// <summary>Graph <c>patternedRecurrence</c>.</summary>
public sealed record PatternedRecurrenceDto
{
    public RecurrencePatternDto? Pattern { get; init; }

    public RecurrenceRangeDto? Range { get; init; }
}

/// <summary>Graph <c>event</c>.</summary>
public sealed record EventDto
{
    public string? Id { get; init; }

    [JsonPropertyName("iCalUId")]
    public string? ICalUId { get; init; }

    public string? Subject { get; init; }

    public string? BodyPreview { get; init; }

    public bool IsAllDay { get; init; }

    public ShowAs ShowAs { get; init; }

    public bool IsOrganizer { get; init; }

    public bool IsReminderOn { get; init; }

    public int ReminderMinutesBeforeStart { get; init; }

    public string? SeriesMasterId { get; init; }

    public ResponseStatusDto? ResponseStatus { get; init; }

    public DateTimeTimeZoneDto? Start { get; init; }

    public DateTimeTimeZoneDto? End { get; init; }

    public LocationDto? Location { get; init; }

    public PatternedRecurrenceDto? Recurrence { get; init; }

    public IReadOnlyList<AttendeeDto>? Attendees { get; init; }

    public RecipientDto? Organizer { get; init; }

    public OnlineMeetingDto? OnlineMeeting { get; init; }
}
