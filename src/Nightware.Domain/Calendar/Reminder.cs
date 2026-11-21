using Nightware.Domain.Kernel;

namespace Nightware.Domain.Calendar;

/// <summary>Graph <c>reminder</c>: an event's reminder that is due.</summary>
public sealed record Reminder(EventId EventId, string? EventSubject, DateTimeTimeZone EventStartTime, DateTimeTimeZone EventEndTime, DateTimeTimeZone ReminderFireTime)
{
    public Location? EventLocation { get; init; }
}
