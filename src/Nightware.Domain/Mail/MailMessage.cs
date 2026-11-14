using Nightware.Domain.Kernel;

namespace Nightware.Domain.Mail;

public enum Importance
{
    Low,
    Normal,
    High,
}

public enum FlagStatus
{
    NotFlagged,
    Flagged,
    Complete,
}

public enum BodyType
{
    Text,
    Html,
}

public sealed record ItemBody(BodyType ContentType, string Content);

public sealed record FollowUpFlag(FlagStatus Status)
{
    public static FollowUpFlag NotFlagged { get; } = new(FlagStatus.NotFlagged);
}

public sealed record InternetHeader(string Name, string Value);

/// <summary>Graph <c>meetingMessageType</c> for invitation-related mail.</summary>
public enum MeetingMessageType
{
    None,
    MeetingRequest,
    MeetingCancelled,
    MeetingAccepted,
    MeetingTentativelyAccepted,
    MeetingDeclined,
}

/// <summary>
/// The <c>text/calendar</c> part of an invitation-related message. Detected by
/// the mail adapter (Phase 2); resolved to a calendar event by Phase 4.
/// </summary>
public sealed record CalendarPart(string Method, AttachmentId PartId)
{
    public MeetingMessageType MessageType => Method.ToUpperInvariant() switch
    {
        "REQUEST" => MeetingMessageType.MeetingRequest,
        "CANCEL" => MeetingMessageType.MeetingCancelled,
        "REPLY" => MeetingMessageType.MeetingAccepted, // refined by PARTSTAT where known
        _ => MeetingMessageType.None,
    };

    /// <summary>True for organizer → attendee messages (Graph <c>eventMessageRequest</c>).</summary>
    public bool IsRequest => !string.Equals(Method, "REPLY", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A mail message. Fields not fetched for a request are left at their defaults.</summary>
public sealed record MailMessage(MessageId Id)
{
    public string? Subject { get; init; }

    public EmailAddress? From { get; init; }

    public EmailAddress? Sender { get; init; }

    public IReadOnlyList<EmailAddress> ToRecipients { get; init; } = [];

    public IReadOnlyList<EmailAddress> CcRecipients { get; init; } = [];

    public IReadOnlyList<EmailAddress> BccRecipients { get; init; } = [];

    public IReadOnlyList<EmailAddress> ReplyTo { get; init; } = [];

    public string? BodyPreview { get; init; }

    public ItemBody? Body { get; init; }

    public ItemBody? UniqueBody { get; init; }

    public bool IsRead { get; init; }

    public bool IsDraft { get; init; }

    public FollowUpFlag Flag { get; init; } = FollowUpFlag.NotFlagged;

    public DateTimeOffset? ReceivedDateTime { get; init; }

    public DateTimeOffset? SentDateTime { get; init; }

    public DateTimeOffset? CreatedDateTime { get; init; }

    public DateTimeOffset? LastModifiedDateTime { get; init; }

    public Importance Importance { get; init; } = Importance.Normal;

    /// <summary>Thread identifier (JMAP <c>threadId</c>).</summary>
    public string? ConversationId { get; init; }

    public bool HasAttachments { get; init; }

    public FolderId? ParentFolderId { get; init; }

    /// <summary>RFC 5322 Message-ID, without angle brackets.</summary>
    public string? InternetMessageId { get; init; }

    public IReadOnlyList<string> Categories { get; init; } = [];

    public IReadOnlyList<InternetHeader> InternetMessageHeaders { get; init; } = [];

    public IReadOnlyList<MailAttachment> Attachments { get; init; } = [];

    public CalendarPart? CalendarPart { get; init; }
}
