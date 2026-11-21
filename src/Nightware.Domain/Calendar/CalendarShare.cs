using Nightware.Domain.Kernel;

namespace Nightware.Domain.Calendar;

/// <summary>What a share lets someone do with a calendar. Nextcloud shares are read or read-write.</summary>
public enum CalendarRole
{
    Read,
    Write,
}

/// <summary>Graph <c>calendarPermission</c>: one person a calendar is shared with (named for CA1711).</summary>
public sealed record CalendarShare(string Id, EmailAddress EmailAddress, CalendarRole Role);
