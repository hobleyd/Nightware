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

/// <summary>
/// An iTIP message (RFC 5546) the façade sends itself by mail (iMIP, RFC 6047) because the calendar server can't say
/// it: a reply with a comment, or a counter-proposal, to an organizer outside the server.
/// </summary>
/// <param name="To">The organizer's address.</param>
/// <param name="Calendar">The iCalendar object, with <c>METHOD:</c><paramref name="Method"/>.</param>
/// <param name="Method"><c>REPLY</c> or <c>COUNTER</c>.</param>
public sealed record ItipMessage(string To, string Subject, string Body, string Calendar, string Method);

/// <summary>Sends an <see cref="ItipMessage"/> from a mailbox, as mail (the mail adapter).</summary>
public interface IItipMail
{
    Task<Result> SendAsync(MailboxRef from, ItipMessage message, CancellationToken cancellationToken);
}

public interface ISchedulingActions
{
    /// <param name="sendResponse">False records the answer without telling the organizer.</param>
    /// <param name="proposedNewTime">Only with decline/tentative: the attendee's counter-proposal (iTIP COUNTER semantics).</param>
    /// <returns>The message the caller must send itself (<see cref="IItipMail"/>), or null when the calendar server sends the reply (or none is wanted).</returns>
    Task<Result<ItipMessage?>> RespondAsync(MailboxRef mailbox, EventId id, RsvpResponse response, string? comment, bool sendResponse, TimeSlot? proposedNewTime, CancellationToken cancellationToken);

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
