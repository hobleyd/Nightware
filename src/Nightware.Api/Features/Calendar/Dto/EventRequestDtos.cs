using Nightware.Domain.Calendar;

namespace Nightware.Api.Features.Calendar.Dto;

// Request bodies. Bound with GraphJson.Options, so keys are case-insensitive (events/{id}/forward sends
// PascalCase) and enums accept any casing.

/// <summary>Graph <c>itemBody</c> as sent on event writes; <c>contentType</c> is <c>text</c> or <c>html</c>.</summary>
public sealed record ItemBodyDto
{
    public string? ContentType { get; init; }

    public string? Content { get; init; }
}

/// <summary>An attendee as sent on event writes.</summary>
public sealed record AttendeeInputDto
{
    public EmailAddressDto? EmailAddress { get; init; }

    public AttendeeType? Type { get; init; }
}

/// <summary>A recurrence pattern as sent on event writes.</summary>
public sealed record RecurrencePatternInputDto
{
    public RecurrencePatternType? Type { get; init; }

    public int? Interval { get; init; }

    public int? Month { get; init; }

    public int? DayOfMonth { get; init; }

    public IReadOnlyList<DayOfWeek>? DaysOfWeek { get; init; }

    public DayOfWeek? FirstDayOfWeek { get; init; }

    public string? Index { get; init; }
}

/// <summary>A recurrence range as sent on event writes.</summary>
public sealed record RecurrenceRangeInputDto
{
    public RecurrenceRangeType? Type { get; init; }

    public DateOnly? StartDate { get; init; }

    public DateOnly? EndDate { get; init; }

    public int? NumberOfOccurrences { get; init; }

    public string? RecurrenceTimeZone { get; init; }
}

/// <summary>A recurrence as sent on event writes.</summary>
public sealed record PatternedRecurrenceInputDto
{
    public RecurrencePatternInputDto? Pattern { get; init; }

    public RecurrenceRangeInputDto? Range { get; init; }
}

/// <summary>
/// The body of <c>POST events</c> and <c>PATCH events/{id}</c>. Absent (or null) members of a PATCH are
/// left unchanged; NightMail's full edit sends every member and clears with empty values
/// (<c>location: {displayName: ""}</c>, <c>attendees: []</c>).
/// </summary>
public sealed record EventWriteDto
{
    public string? Subject { get; init; }

    public ItemBodyDto? Body { get; init; }

    public DateTimeTimeZoneDto? Start { get; init; }

    public DateTimeTimeZoneDto? End { get; init; }

    public bool? IsAllDay { get; init; }

    public bool? IsReminderOn { get; init; }

    public int? ReminderMinutesBeforeStart { get; init; }

    public LocationDto? Location { get; init; }

    public IReadOnlyList<LocationDto>? Locations { get; init; }

    public IReadOnlyList<AttendeeInputDto>? Attendees { get; init; }

    private readonly PatternedRecurrenceInputDto? _recurrence;
    private readonly bool _recurrenceSent;

    /// <summary>On a PATCH, an explicit <c>null</c> clears the recurrence (<see cref="RecurrenceSent"/>); absent leaves it.</summary>
    public PatternedRecurrenceInputDto? Recurrence
    {
        get => _recurrence;
        init
        {
            _recurrence = value;
            _recurrenceSent = true;
        }
    }

    /// <summary>True when the body named <c>recurrence</c> at all, null included.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool RecurrenceSent => _recurrenceSent;

    public ShowAs? ShowAs { get; init; }

    public IReadOnlyList<string>? Categories { get; init; }

    /// <summary>
    /// <c>true</c> gives the event a meeting room (Nextcloud Talk) whose link becomes <c>onlineMeeting.joinUrl</c>;
    /// <c>false</c> is ignored, as Graph ignores it once a meeting is online.
    /// </summary>
    public bool? IsOnlineMeeting { get; init; }

    /// <summary>Accepted and ignored: every online meeting is a Talk room, whatever provider is named.</summary>
    public string? OnlineMeetingProvider { get; init; }
}

/// <summary>Graph <c>timeSlot</c> (a proposed new meeting time).</summary>
public sealed record TimeSlotDto
{
    public DateTimeTimeZoneDto? Start { get; init; }

    public DateTimeTimeZoneDto? End { get; init; }
}

/// <summary>The body of <c>accept</c>, <c>tentativelyAccept</c> and <c>decline</c> (event or message).</summary>
public sealed record RsvpBodyDto
{
    /// <summary>Graph's default is true.</summary>
    public bool? SendResponse { get; init; }

    public string? Comment { get; init; }

    public TimeSlotDto? ProposedNewTime { get; init; }
}

/// <summary>The body of <c>events/{id}/cancel</c>.</summary>
public sealed record CancelBodyDto
{
    public string? Comment { get; init; }
}

/// <summary>The body of <c>events/{id}/forward</c> (NightMail sends <c>ToRecipients</c>/<c>Comment</c> in PascalCase).</summary>
public sealed record ForwardBodyDto
{
    public IReadOnlyList<RecipientDto>? ToRecipients { get; init; }

    public string? Comment { get; init; }
}
