using Nightware.Domain.Mail;

namespace Nightware.Application.Mail.Read;

/// <summary>
/// The meeting facts of an invitation-related message, read from its <c>text/calendar</c> part
/// (ADR 0001 §33). Instants are absolute; the Api expresses them in the <c>Prefer</c> time zone.
/// </summary>
public sealed record MeetingDetails
{
    public DateTimeOffset? Start { get; init; }

    public DateTimeOffset? End { get; init; }

    public bool IsAllDay { get; init; }

    public string? Location { get; init; }

    /// <summary>The refined type: a REPLY's comes from the attendee's PARTSTAT, a COUNTER is a decline with a proposal.</summary>
    public MeetingMessageType MessageType { get; init; }

    /// <summary>A counter-proposal's new start (iTIP COUNTER).</summary>
    public DateTimeOffset? ProposedStart { get; init; }

    public DateTimeOffset? ProposedEnd { get; init; }
}

/// <summary>A single message as the reading pane shows it: the message plus, for invitation mail, its meeting facts.</summary>
public sealed record MessageView(MailMessage Message, MeetingDetails? Meeting = null);
