using System.Text.Json.Serialization;
using Nightware.Domain.Calendar;

namespace Nightware.Api.Features.Calendar.Dto;

/// <summary>The body of <c>POST calendar/getSchedule</c>.</summary>
public sealed record ScheduleRequestDto
{
    public IReadOnlyList<string>? Schedules { get; init; }

    public DateTimeTimeZoneDto? StartTime { get; init; }

    public DateTimeTimeZoneDto? EndTime { get; init; }

    public int? AvailabilityViewInterval { get; init; }
}

/// <summary>Graph <c>scheduleItem</c>. Plain free/busy has no subject or location, so those are omitted when unknown.</summary>
public sealed record ScheduleItemDto
{
    public bool IsPrivate { get; init; }

    public FreeBusyStatus Status { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Subject { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Location { get; init; }

    public DateTimeTimeZoneDto? Start { get; init; }

    public DateTimeTimeZoneDto? End { get; init; }
}

/// <summary>Graph <c>freeBusyError</c>.</summary>
public sealed record FreeBusyErrorDto
{
    public string? Message { get; init; }

    public string? ResponseCode { get; init; }
}

/// <summary>Graph <c>scheduleInformation</c>.</summary>
public sealed record ScheduleInformationDto
{
    public string? ScheduleId { get; init; }

    public string? AvailabilityView { get; init; }

    public IReadOnlyList<ScheduleItemDto>? ScheduleItems { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FreeBusyErrorDto? Error { get; init; }
}

/// <summary>The body of <c>POST findMeetingTimes</c>. <c>locationConstraint</c> is accepted and ignored: no rooms are suggested.</summary>
public sealed record MeetingTimeRequestDto
{
    public IReadOnlyList<AttendeeDto>? Attendees { get; init; }

    public TimeConstraintDto? TimeConstraint { get; init; }

    /// <summary>ISO 8601 duration, e.g. <c>PT1H</c>.</summary>
    public string? MeetingDuration { get; init; }

    public int? MaxCandidates { get; init; }

    public bool? IsOrganizerOptional { get; init; }

    public double? MinimumAttendeePercentage { get; init; }
}

/// <summary>Graph <c>timeConstraint</c>.</summary>
public sealed record TimeConstraintDto
{
    public string? ActivityDomain { get; init; }

    public IReadOnlyList<TimeSlotDto>? TimeSlots { get; init; }
}

/// <summary>Graph <c>attendeeAvailability</c>.</summary>
public sealed record AttendeeAvailabilityDto
{
    public AttendeeBaseDto? Attendee { get; init; }

    public FreeBusyStatus Availability { get; init; }
}

/// <summary>Graph <c>attendeeBase</c>.</summary>
public sealed record AttendeeBaseDto
{
    public AttendeeType Type { get; init; }

    public EmailAddressDto? EmailAddress { get; init; }
}

/// <summary>Graph <c>meetingTimeSuggestion</c>.</summary>
public sealed record MeetingTimeSuggestionDto
{
    public double Confidence { get; init; }

    public FreeBusyStatus OrganizerAvailability { get; init; }

    public IReadOnlyList<AttendeeAvailabilityDto>? AttendeeAvailability { get; init; }

    public IReadOnlyList<LocationDto>? Locations { get; init; }

    public TimeSlotDto? MeetingTimeSlot { get; init; }
}

/// <summary>Graph <c>meetingTimeSuggestionsResult</c>.</summary>
public sealed record MeetingTimeSuggestionsResultDto
{
    public string? EmptySuggestionsReason { get; init; }

    public IReadOnlyList<MeetingTimeSuggestionDto>? MeetingTimeSuggestions { get; init; }
}
