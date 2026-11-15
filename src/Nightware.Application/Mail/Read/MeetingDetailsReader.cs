using Nightware.Application.Calendar;
using Nightware.Application.Common.Time;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail.Read;

/// <summary>
/// Turns a parsed invitation (<see cref="CalendarDocument"/>) into the <see cref="MeetingDetails"/>
/// of the message that carried it, and refines the message's <see cref="CalendarPart"/>.
/// </summary>
internal static class MeetingDetailsReader
{
    private const string Counter = "COUNTER";

    /// <summary>
    /// The facts of <paramref name="document"/>, or null when it holds no event. The returned part
    /// carries the replying attendee's PARTSTAT for a REPLY or COUNTER.
    /// </summary>
    public static (CalendarPart Part, MeetingDetails Details)? Read(CalendarPart part, CalendarDocument document, EmailAddress? from)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(document);

        // A series' master (or a lone event) describes the meeting; a document holding only
        // exceptions (an update to one occurrence) uses its first.
        var meeting = document.Events.FirstOrDefault(e => e.OriginalStart is null && !e.IsCancelled)
            ?? (document.Events.Count > 0 ? document.Events[0] : null);
        if (meeting is null)
        {
            return null;
        }

        var isReply = part.Method.Equals("REPLY", StringComparison.OrdinalIgnoreCase);
        var isCounter = part.Method.Equals(Counter, StringComparison.OrdinalIgnoreCase);
        var refined = isReply || isCounter ? part with { ReplyPartStat = PartStatOf(Responder(meeting, from)) } : part;
        var start = ToInstant(meeting.Start);
        var end = ToInstant(meeting.End);
        var details = new MeetingDetails
        {
            IsAllDay = meeting.IsAllDay,
            Location = string.IsNullOrWhiteSpace(meeting.Location?.DisplayName) ? null : meeting.Location.DisplayName,
            MessageType = refined.MessageType,
        };

        // Graph models a counter-proposal as a decline carrying proposedNewTime (the client keys its
        // "proposed new time" banner on exactly that). The COUNTER's own DTSTART/DTEND are the
        // proposal; the original time is not in it, so start/end stay unknown.
        return isCounter
            ? (refined, details with { MessageType = MeetingMessageType.MeetingDeclined, ProposedStart = start, ProposedEnd = end })
            : (refined, details with { Start = start, End = end });
    }

    /// <summary>The attendee a REPLY/COUNTER speaks for: the sender, else the only (or first answering) attendee.</summary>
    private static Attendee? Responder(CalendarEvent meeting, EmailAddress? from)
    {
        var attendees = meeting.Attendees;
        if (from is not null
            && attendees.FirstOrDefault(a => string.Equals(a.EmailAddress.Address, from.Address, StringComparison.OrdinalIgnoreCase)) is { } sender)
        {
            return sender;
        }

        return attendees.Count == 1
            ? attendees[0]
            : attendees.FirstOrDefault(a => a.Status.Response is ResponseType.Accepted or ResponseType.TentativelyAccepted or ResponseType.Declined);
    }

    private static string? PartStatOf(Attendee? attendee) => attendee?.Status.Response switch
    {
        ResponseType.Accepted => "ACCEPTED",
        ResponseType.TentativelyAccepted => "TENTATIVE",
        ResponseType.Declined => "DECLINED",
        ResponseType.NotResponded => "NEEDS-ACTION",
        _ => null,
    };

    private static DateTimeOffset ToInstant(DateTimeTimeZone value) => GraphTimeZone.ToInstant(value.DateTime, value.TimeZone);
}
