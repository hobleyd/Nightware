using Nightware.Domain.Kernel;

namespace Nightware.Domain.Calendar;

/// <summary>Graph <c>calendar</c>: one of a mailbox's event calendars.</summary>
public sealed record EventCalendar(CalendarId Id, string Name)
{
    /// <summary><c>#RRGGBB</c>, or null when the calendar has no colour of its own.</summary>
    public string? HexColor { get; init; }

    public bool IsDefault { get; init; }

    public bool CanEdit { get; init; }

    /// <summary>The caller has shared it with others.</summary>
    public bool IsShared { get; init; }

    /// <summary>Someone else's calendar, shared with the caller.</summary>
    public bool IsSharedWithMe { get; init; }

    public bool IsRemovable { get; init; }

    public EmailAddress? Owner { get; init; }
}
