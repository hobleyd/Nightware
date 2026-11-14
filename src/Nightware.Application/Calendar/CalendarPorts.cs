using Nightware.Application.Common.Query;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Calendar;

public interface ICalendarReader
{
    /// <summary>Occurrences overlapping [start, end), recurrences expanded (Graph <c>calendarView</c>).</summary>
    Task<Result<Page<CalendarEvent>>> CalendarViewAsync(MailboxRef mailbox, DateTimeOffset start, DateTimeOffset end, ItemQuery query, CancellationToken cancellationToken);

    /// <summary>Works for single events, series masters and occurrence ids.</summary>
    Task<Result<CalendarEvent>> GetAsync(MailboxRef mailbox, EventId id, CancellationToken cancellationToken);

    /// <summary>Unexpanded events (e.g. <c>$filter=iCalUId eq '…'</c>).</summary>
    Task<Result<Page<CalendarEvent>>> ListAsync(MailboxRef mailbox, ItemQuery query, CancellationToken cancellationToken);
}

/// <summary>Event writes. Occurrence ids mean "this occurrence only" (ADR 0001 §4).</summary>
public interface ICalendarWriter
{
    Task<Result<CalendarEvent>> CreateAsync(MailboxRef mailbox, NewEvent newEvent, CancellationToken cancellationToken);

    Task<Result<CalendarEvent>> UpdateAsync(MailboxRef mailbox, EventId id, EventPatch patch, CancellationToken cancellationToken);

    Task<Result> DeleteAsync(MailboxRef mailbox, EventId id, CancellationToken cancellationToken);
}

public interface ISchedulingActions
{
    Task<Result> RespondAsync(MailboxRef mailbox, EventId id, RsvpResponse response, string? comment, bool sendResponse, CancellationToken cancellationToken);

    /// <summary>Organizer cancels and notifies attendees (iTIP CANCEL).</summary>
    Task<Result> CancelAsync(MailboxRef mailbox, EventId id, string? comment, CancellationToken cancellationToken);

    Task<Result> ForwardAsync(MailboxRef mailbox, EventId id, IReadOnlyList<EmailAddress> recipients, string? comment, CancellationToken cancellationToken);
}

public interface IFreeBusyReader
{
    /// <summary>Busy periods per address; <c>availabilityView</c> is computed by the use case.</summary>
    Task<Result<IReadOnlyList<FreeBusySchedule>>> GetAsync(MailboxRef mailbox, IReadOnlyList<string> addresses, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken);
}

/// <summary>iCalendar (RFC 5545) parsing/serialisation, kept behind a port so use cases never see Ical.Net.</summary>
public interface IICalendarCodec
{
    Result<CalendarDocument> Parse(string ics);

    string Serialize(CalendarDocument document);
}
