namespace Nightware.Domain.Calendar;

/// <summary>A meeting room to open for an event: the event's subject, and how long the room is needed.</summary>
/// <param name="Subject">The event's subject; the room is named after it.</param>
/// <param name="Start">When the meeting starts.</param>
/// <param name="End">When the meeting ends, after which the provider may let the room go; null keeps it until it is closed (a series).</param>
public sealed record OnlineMeetingRequest(string? Subject, DateTimeOffset Start, DateTimeOffset? End);
