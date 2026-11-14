using Nightware.Domain.Kernel;

namespace Nightware.Domain.Calendar;

/// <summary>Graph <c>dateTimeTimeZone</c>: a wall-clock time and the zone it is expressed in.</summary>
public sealed record DateTimeTimeZone(DateTime DateTime, string TimeZone)
{
    public static DateTimeTimeZone Utc(DateTimeOffset value) => new(value.UtcDateTime, "UTC");
}

public enum EventType
{
    SingleInstance,
    Occurrence,
    Exception,
    SeriesMaster,
}

public enum ResponseType
{
    None,
    Organizer,
    TentativelyAccepted,
    Accepted,
    Declined,
    NotResponded,
}

public enum ShowAs
{
    Free,
    Tentative,
    Busy,
    Oof,
    WorkingElsewhere,
    Unknown,
}

public enum AttendeeType
{
    Required,
    Optional,
    Resource,
}

public sealed record ResponseStatus(ResponseType Response, DateTimeOffset? Time = null);

public sealed record Attendee(EmailAddress EmailAddress, AttendeeType Type, ResponseStatus Status);

public sealed record Location(string DisplayName)
{
    public string? LocationEmailAddress { get; init; }
}

public enum RecurrencePatternType
{
    Daily,
    Weekly,
    AbsoluteMonthly,
    RelativeMonthly,
    AbsoluteYearly,
    RelativeYearly,
}

public enum RecurrenceRangeType
{
    EndDate,
    NoEnd,
    Numbered,
}

public sealed record RecurrencePattern(RecurrencePatternType Type, int Interval)
{
    public IReadOnlyList<DayOfWeek> DaysOfWeek { get; init; } = [];

    public int DayOfMonth { get; init; }

    public int Month { get; init; }

    public string? Index { get; init; }

    public DayOfWeek FirstDayOfWeek { get; init; } = DayOfWeek.Sunday;
}

public sealed record RecurrenceRange(RecurrenceRangeType Type, DateOnly StartDate)
{
    public DateOnly? EndDate { get; init; }

    public int NumberOfOccurrences { get; init; }

    public string? RecurrenceTimeZone { get; init; }
}

public sealed record PatternedRecurrence(RecurrencePattern Pattern, RecurrenceRange Range);

public sealed record CalendarEvent(EventId Id, string? Subject, DateTimeTimeZone Start, DateTimeTimeZone End)
{
    /// <summary>iCalendar UID.</summary>
    public string? ICalUId { get; init; }

    public EventType Type { get; init; } = EventType.SingleInstance;

    public EventId? SeriesMasterId { get; init; }

    /// <summary>Original start of an occurrence/exception (iCalendar RECURRENCE-ID).</summary>
    public DateTimeOffset? OriginalStart { get; init; }

    public bool IsAllDay { get; init; }

    public bool IsCancelled { get; init; }

    public bool IsOrganizer { get; init; }

    public string? BodyHtml { get; init; }

    public string? BodyPreview { get; init; }

    public Location? Location { get; init; }

    public IReadOnlyList<Location> Locations { get; init; } = [];

    public EmailAddress? Organizer { get; init; }

    public IReadOnlyList<Attendee> Attendees { get; init; } = [];

    public ResponseStatus ResponseStatus { get; init; } = new(ResponseType.None);

    public ShowAs ShowAs { get; init; } = ShowAs.Busy;

    public PatternedRecurrence? Recurrence { get; init; }

    public bool IsReminderOn { get; init; }

    public int ReminderMinutesBeforeStart { get; init; }

    public bool IsOnlineMeeting { get; init; }

    public string? OnlineMeetingUrl { get; init; }

    public IReadOnlyList<string> Categories { get; init; } = [];

    public string? Sensitivity { get; init; }

    public DateTimeOffset? CreatedDateTime { get; init; }

    public DateTimeOffset? LastModifiedDateTime { get; init; }

    public string? WebLink { get; init; }

    /// <summary>iCalendar SEQUENCE.</summary>
    public int Sequence { get; init; }
}
