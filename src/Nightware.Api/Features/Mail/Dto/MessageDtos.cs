using System.Text.Json.Serialization;

namespace Nightware.Api.Features.Mail.Dto;

// The Graph message DTO family (frozen, ADR 0001 §24). Members are exactly what NightMail reads or
// sends (graph_message_parser.dart, email_model.dart, the datasource's $select lists and bodies).
// Property names are camelCased by GraphJson; enums are Graph's camelCase strings. Members Graph
// leaves out of a default response carry WhenWritingNull so they only appear when populated.

/// <summary>Graph <c>emailAddress</c>.</summary>
public sealed record EmailAddressDto
{
    public string? Name { get; init; }

    public string? Address { get; init; }
}

/// <summary>Graph <c>recipient</c>: <c>{"emailAddress": {...}}</c>.</summary>
public sealed record RecipientDto
{
    public EmailAddressDto? EmailAddress { get; init; }
}

/// <summary>Graph <c>itemBody</c>. <c>contentType</c> is <c>html</c> or <c>text</c> (input is case-insensitive).</summary>
public sealed record ItemBodyDto
{
    public string? ContentType { get; init; }

    public string? Content { get; init; }
}

/// <summary>Graph <c>followupFlag</c>; the client reads only <c>flagStatus</c>.</summary>
public sealed record FollowupFlagDto
{
    /// <summary><c>notFlagged</c>, <c>flagged</c> or <c>complete</c>.</summary>
    public string? FlagStatus { get; init; }
}

/// <summary>Graph <c>internetMessageHeader</c>.</summary>
public sealed record InternetMessageHeaderDto
{
    public string? Name { get; init; }

    public string? Value { get; init; }
}

/// <summary>Graph <c>dateTimeTimeZone</c>: wall clock with seven fractional digits and no <c>Z</c>, plus the zone label.</summary>
public sealed record DateTimeTimeZoneDto
{
    public DateTime DateTime { get; init; }

    public string? TimeZone { get; init; }
}

/// <summary>Graph <c>timeSlot</c> (<c>eventMessageResponse.proposedNewTime</c>).</summary>
public sealed record TimeSlotDto
{
    public DateTimeTimeZoneDto? Start { get; init; }

    public DateTimeTimeZoneDto? End { get; init; }
}

/// <summary>Graph <c>location</c>; the client reads only <c>displayName</c>.</summary>
public sealed record LocationDto
{
    public string? DisplayName { get; init; }
}

/// <summary>
/// Graph <c>singleValueLegacyExtendedProperty</c>. Write-only: account migration sends
/// <c>SystemTime 0x0E06</c>/<c>SystemTime 0x0039</c> (received/sent) and <c>Integer 0x0E07</c> (PR_MESSAGE_FLAGS).
/// </summary>
public sealed record SingleValueExtendedPropertyDto
{
    public string? Id { get; init; }

    public string? Value { get; init; }
}

/// <summary>Graph <c>message</c>.</summary>
public record MessageDto
{
    public string? Id { get; init; }

    public DateTimeOffset? CreatedDateTime { get; init; }

    public DateTimeOffset? LastModifiedDateTime { get; init; }

    public IReadOnlyList<string>? Categories { get; init; }

    public DateTimeOffset? ReceivedDateTime { get; init; }

    public DateTimeOffset? SentDateTime { get; init; }

    public bool? HasAttachments { get; init; }

    public string? InternetMessageId { get; init; }

    public string? Subject { get; init; }

    public string? BodyPreview { get; init; }

    /// <summary><c>low</c>, <c>normal</c> or <c>high</c>.</summary>
    public string? Importance { get; init; }

    public string? ParentFolderId { get; init; }

    public string? ConversationId { get; init; }

    public bool? IsRead { get; init; }

    public bool? IsDraft { get; init; }

    public ItemBodyDto? Body { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ItemBodyDto? UniqueBody { get; init; }

    public RecipientDto? Sender { get; init; }

    public RecipientDto? From { get; init; }

    public IReadOnlyList<RecipientDto>? ToRecipients { get; init; }

    public IReadOnlyList<RecipientDto>? CcRecipients { get; init; }

    public IReadOnlyList<RecipientDto>? BccRecipients { get; init; }

    public IReadOnlyList<RecipientDto>? ReplyTo { get; init; }

    public FollowupFlagDto? Flag { get; init; }

    /// <summary>Only returned when explicitly <c>$select</c>ed (Graph's default projection omits it).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<InternetMessageHeaderDto>? InternetMessageHeaders { get; init; }

    /// <summary>Only returned with <c>$expand=attachments</c>; also accepted on draft/send input.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<AttachmentDto>? Attachments { get; init; }

    /// <summary>Input only (migration). Never emitted.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SingleValueExtendedPropertyDto>? SingleValueExtendedProperties { get; init; }
}

/// <summary>
/// Graph <c>eventMessage</c> and its <c>eventMessageRequest</c>/<c>eventMessageResponse</c> flavours,
/// told apart by <see cref="ODataType"/>. The client reads <c>meetingMessageType</c>,
/// <c>startDateTime</c>, <c>endDateTime</c>, <c>location</c>, <c>isAllDay</c> and <c>proposedNewTime</c>.
/// </summary>
public sealed record EventMessageDto : MessageDto
{
    public const string EventMessageODataType = "#microsoft.graph.eventMessage";
    public const string RequestODataType = "#microsoft.graph.eventMessageRequest";
    public const string ResponseODataType = "#microsoft.graph.eventMessageResponse";

    public EventMessageDto()
    {
    }

    /// <summary>Copies every <c>message</c> member of <paramref name="message"/>.</summary>
    public EventMessageDto(MessageDto message)
        : base(message)
    {
    }

    [JsonPropertyName("@odata.type")]
    [JsonPropertyOrder(-1)]
    public string ODataType { get; init; } = EventMessageODataType;

    /// <summary><c>meetingRequest</c>, <c>meetingCancelled</c>, <c>meetingAccepted</c>, <c>meetingTentativelyAccepted</c>, <c>meetingDeclined</c>.</summary>
    public string? MeetingMessageType { get; init; }

    public DateTimeTimeZoneDto? StartDateTime { get; init; }

    public DateTimeTimeZoneDto? EndDateTime { get; init; }

    public LocationDto? Location { get; init; }

    public bool? IsAllDay { get; init; }

    /// <summary>Response flavour only: <c>accepted</c>, <c>tentativelyAccepted</c>, <c>declined</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResponseType { get; init; }

    /// <summary>Response flavour only: a counter-proposal (a decline carrying a new time).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TimeSlotDto? ProposedNewTime { get; init; }
}

/// <summary>
/// Graph <c>attachment</c> (<c>fileAttachment</c> / <c>itemAttachment</c>). Base properties are
/// <c>id name contentType size isInline lastModifiedDateTime</c>; <c>contentId</c>/<c>contentBytes</c>
/// exist on <c>fileAttachment</c> only.
/// </summary>
public sealed record AttachmentDto
{
    public const string FileAttachmentType = "#microsoft.graph.fileAttachment";
    public const string ItemAttachmentType = "#microsoft.graph.itemAttachment";

    [JsonPropertyName("@odata.type")]
    [JsonPropertyOrder(-1)]
    public string? ODataType { get; init; }

    public string? Id { get; init; }

    public DateTimeOffset? LastModifiedDateTime { get; init; }

    public string? Name { get; init; }

    public string? ContentType { get; init; }

    public long? Size { get; init; }

    public bool? IsInline { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContentId { get; init; }

    /// <summary>Base64 bytes; only when the request asked for them (single-attachment GET).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContentBytes { get; init; }
}

/// <summary>Graph <c>mailFolder</c>, exactly the properties the client selects.</summary>
public sealed record MailFolderDto
{
    public string? Id { get; init; }

    public string? DisplayName { get; init; }

    public string? ParentFolderId { get; init; }

    public int ChildFolderCount { get; init; }

    public int UnreadItemCount { get; init; }

    public int TotalItemCount { get; init; }

    public bool IsHidden { get; init; }
}
