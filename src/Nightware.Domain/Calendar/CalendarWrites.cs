using Nightware.Domain.Kernel;

namespace Nightware.Domain.Calendar;

/// <summary>An event being created.</summary>
public sealed record NewEvent(string? Subject, DateTimeTimeZone Start, DateTimeTimeZone End)
{
    public bool IsAllDay { get; init; }

    public string? BodyHtml { get; init; }

    public Location? Location { get; init; }

    public IReadOnlyList<Location> Locations { get; init; } = [];

    public IReadOnlyList<Attendee> Attendees { get; init; } = [];

    public ShowAs ShowAs { get; init; } = ShowAs.Busy;

    public PatternedRecurrence? Recurrence { get; init; }

    public bool IsReminderOn { get; init; } = true;

    public int ReminderMinutesBeforeStart { get; init; } = 15;

    public bool IsOnlineMeeting { get; init; }

    public IReadOnlyList<string> Categories { get; init; } = [];

    public string? TransactionId { get; init; }
}

/// <summary>A partial update to an event. Null members are left unchanged.</summary>
public sealed record EventPatch
{
    public string? Subject { get; init; }

    public DateTimeTimeZone? Start { get; init; }

    public DateTimeTimeZone? End { get; init; }

    public bool? IsAllDay { get; init; }

    public string? BodyHtml { get; init; }

    public Location? Location { get; init; }

    public IReadOnlyList<Location>? Locations { get; init; }

    public IReadOnlyList<Attendee>? Attendees { get; init; }

    public ShowAs? ShowAs { get; init; }

    public PatternedRecurrence? Recurrence { get; init; }

    /// <summary>
    /// <c>"recurrence": null</c>: the series becomes a single event at its first occurrence's time, and its
    /// modified and cancelled occurrences go, as Graph does.
    /// </summary>
    public bool ClearRecurrence { get; init; }

    public bool? IsReminderOn { get; init; }

    public int? ReminderMinutesBeforeStart { get; init; }

    public IReadOnlyList<string>? Categories { get; init; }

    /// <summary>True when the change alters timing, so attendees must be re-invited (SEQUENCE bump).</summary>
    public bool ChangesSchedule => Start is not null || End is not null || IsAllDay is not null || Recurrence is not null || ClearRecurrence;
}

public enum RsvpResponse
{
    Accept,
    TentativelyAccept,
    Decline,
}

public enum FreeBusyStatus
{
    Free = 0,
    Tentative = 1,
    Busy = 2,
    Oof = 3,
    WorkingElsewhere = 4,
    Unknown = -1,
}

/// <summary>One busy period of a schedule (Graph <c>scheduleItem</c>).</summary>
public sealed record ScheduleItem(DateTimeOffset Start, DateTimeOffset End, FreeBusyStatus Status)
{
    /// <summary>The event's subject, when the backend discloses it (plain free/busy does not).</summary>
    public string? Subject { get; init; }

    public string? Location { get; init; }

    public bool IsPrivate { get; init; }
}

/// <summary>Free/busy for one address (Graph <c>scheduleInformation</c>, before <c>availabilityView</c> is computed).</summary>
public sealed record FreeBusySchedule(string ScheduleId, IReadOnlyList<ScheduleItem> Items)
{
    /// <summary>Set when the address could not be resolved.</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Graph <c>scheduleInformation</c>: a schedule's busy periods plus <c>availabilityView</c>, one
/// digit per interval of the requested window (0 free, 1 tentative, 2 busy, 3 oof, 4 working elsewhere).
/// </summary>
public sealed record ScheduleInformation(string ScheduleId, string AvailabilityView, IReadOnlyList<ScheduleItem> Items)
{
    /// <summary>Set when the schedule could not be read (unknown address, no permission).</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>A parsed iCalendar object (VCALENDAR) in provider-neutral form.</summary>
public sealed record CalendarDocument(string? Method, IReadOnlyList<CalendarEvent> Events);
