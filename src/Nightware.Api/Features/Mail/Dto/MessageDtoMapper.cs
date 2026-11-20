using Nightware.Api.Graph;
using Nightware.Application.Common.Time;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Api.Features.Mail.Dto;

/// <summary>
/// The meeting facts of an invitation-related message that <see cref="MailMessage"/> does not carry
/// (its <see cref="CalendarPart"/> holds only the method and part id). Whoever resolves the calendar
/// part (Phase 2/4, via <c>IICalendarCodec</c>) supplies them; without them the client shows an
/// invitation banner with no time.
/// </summary>
public sealed record MeetingMessageDetails
{
    public DateTimeOffset? Start { get; init; }

    public DateTimeOffset? End { get; init; }

    public bool IsAllDay { get; init; }

    public string? Location { get; init; }

    /// <summary>Overrides <see cref="CalendarPart.MessageType"/> (e.g. a REPLY whose PARTSTAT is DECLINED).</summary>
    public MeetingMessageType? MessageType { get; init; }

    public DateTimeOffset? ProposedStart { get; init; }

    public DateTimeOffset? ProposedEnd { get; init; }
}

/// <summary>What the request asked for, beyond the message itself.</summary>
/// <param name="Select">The request's <c>$select</c>; <c>internetMessageHeaders</c> is emitted only when it names it.</param>
/// <param name="IncludeAttachments">True with <c>$expand=attachments</c>.</param>
/// <param name="Prefer">Time zone for <c>dateTimeTimeZone</c> values (<c>Prefer: outlook.timezone</c>).</param>
public sealed record MessageMappingOptions(
    IReadOnlySet<string>? Select = null,
    bool IncludeAttachments = false,
    PreferHeader? Prefer = null,
    MeetingMessageDetails? Meeting = null)
{
    public static MessageMappingOptions Default { get; } = new();
}

/// <summary>Maps Domain mail types to the Graph DTOs.</summary>
public static class MessageDtoMapper
{
    public const string InternetMessageHeadersProperty = "internetMessageHeaders";

    public static MessageDto ToDto(MailMessage message, MessageMappingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        options ??= MessageMappingOptions.Default;

        var includeHeaders = options.Select?.Any(static s => string.Equals(s, InternetMessageHeadersProperty, StringComparison.OrdinalIgnoreCase)) == true;

        var dto = new MessageDto
        {
            Id = message.Id.Value,
            CreatedDateTime = message.CreatedDateTime,
            LastModifiedDateTime = message.LastModifiedDateTime,
            Categories = message.Categories,
            ReceivedDateTime = message.ReceivedDateTime,
            SentDateTime = message.SentDateTime,
            HasAttachments = message.HasAttachments,
            InternetMessageId = message.InternetMessageId is null ? null : $"<{message.InternetMessageId}>",
            Subject = message.Subject,
            BodyPreview = message.BodyPreview,
            Importance = ToGraph(message.Importance),
            ParentFolderId = message.ParentFolderId?.Value,
            ConversationId = message.ConversationId,
            IsRead = message.IsRead,
            IsDraft = message.IsDraft,
            Body = ToDto(message.Body),
            UniqueBody = message.UniqueBody is null ? null : ToDto(message.UniqueBody),
            Sender = ToRecipient(message.Sender ?? message.From),
            From = ToRecipient(message.From),
            ToRecipients = message.ToRecipients.Select(ToRecipientDto).ToList(),
            CcRecipients = message.CcRecipients.Select(ToRecipientDto).ToList(),
            BccRecipients = message.BccRecipients.Select(ToRecipientDto).ToList(),
            ReplyTo = message.ReplyTo.Select(ToRecipientDto).ToList(),
            Flag = new FollowupFlagDto
            {
                FlagStatus = ToGraph(message.Flag.Status),
                StartDateTime = ToDto(message.Flag.StartDateTime),
                DueDateTime = ToDto(message.Flag.DueDateTime),
                CompletedDateTime = ToDto(message.Flag.CompletedDateTime),
            },
            InternetMessageHeaders = includeHeaders
                ? message.InternetMessageHeaders.Select(static h => new InternetMessageHeaderDto { Name = h.Name, Value = h.Value }).ToList()
                : null,
            Attachments = options.IncludeAttachments ? message.Attachments.Select(ToDto).ToList() : null,
        };

        return message.CalendarPart is { } part ? ToEventMessage(dto, part, options) : dto;
    }

    public static AttachmentDto ToDto(MailAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        var isFile = attachment.Kind == AttachmentKind.File;
        var contentType = !isFile && string.IsNullOrWhiteSpace(attachment.ContentType) ? "message/rfc822" : attachment.ContentType;
        return new AttachmentDto
        {
            ODataType = isFile ? AttachmentDto.FileAttachmentType : AttachmentDto.ItemAttachmentType,
            Id = attachment.Id.Value,
            LastModifiedDateTime = attachment.LastModifiedDateTime,
            Name = attachment.Name,
            ContentType = contentType,
            Size = attachment.Size,
            IsInline = attachment.IsInline,
            ContentId = isFile ? attachment.ContentId : null,
            ContentBytes = isFile && attachment.ContentBytes is { } bytes ? Convert.ToBase64String(bytes.Span) : null,
        };
    }

    public static MailFolderDto ToDto(MailFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return new MailFolderDto
        {
            Id = folder.Id.Value,
            DisplayName = folder.DisplayName,
            ParentFolderId = folder.ParentFolderId?.Value,
            ChildFolderCount = folder.ChildFolderCount,
            UnreadItemCount = folder.UnreadItemCount,
            TotalItemCount = folder.TotalItemCount,
            IsHidden = folder.IsHidden,
        };
    }

    public static ItemBodyDto? ToDto(ItemBody? body) =>
        body is null ? null : new ItemBodyDto { ContentType = body.ContentType == BodyType.Html ? "html" : "text", Content = body.Content };

    public static RecipientDto ToRecipientDto(EmailAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return new RecipientDto { EmailAddress = new EmailAddressDto { Name = address.Name ?? address.Address, Address = address.Address } };
    }

    /// <summary>Graph enum spelling for a meeting message type.</summary>
    public static string ToGraph(MeetingMessageType type) => type switch
    {
        MeetingMessageType.MeetingRequest => "meetingRequest",
        MeetingMessageType.MeetingCancelled => "meetingCancelled",
        MeetingMessageType.MeetingAccepted => "meetingAccepted",
        MeetingMessageType.MeetingTentativelyAccepted => "meetingTentativelyAccepted",
        MeetingMessageType.MeetingDeclined => "meetingDeclined",
        _ => "none",
    };

    public static string ToGraph(Importance importance) => importance switch
    {
        Importance.Low => "low",
        Importance.High => "high",
        _ => "normal",
    };

    private static DateTimeTimeZoneDto? ToDto(Domain.Calendar.DateTimeTimeZone? value) =>
        value is null ? null : new DateTimeTimeZoneDto { DateTime = value.DateTime, TimeZone = value.TimeZone };

    public static string ToGraph(FlagStatus status) => status switch
    {
        FlagStatus.Flagged => "flagged",
        FlagStatus.Complete => "complete",
        _ => "notFlagged",
    };

    /// <summary>An instant as <c>dateTimeTimeZone</c> in the preferred zone (UTC by default).</summary>
    public static DateTimeTimeZoneDto ToDateTimeTimeZone(DateTimeOffset instant, PreferHeader? prefer)
    {
        var zone = prefer?.TimeZone ?? TimeZoneInfo.Utc;
        return new DateTimeTimeZoneDto
        {
            DateTime = GraphTimeZone.ToWallClock(instant, zone),
            TimeZone = prefer?.TimeZoneLabel ?? GraphTimeZone.Utc,
        };
    }

    private static MessageDto ToEventMessage(MessageDto message, CalendarPart part, MessageMappingOptions options)
    {
        var meeting = options.Meeting;
        var type = meeting?.MessageType ?? part.MessageType;
        if (type == MeetingMessageType.None)
        {
            return message;
        }

        var isResponse = type is MeetingMessageType.MeetingAccepted or MeetingMessageType.MeetingTentativelyAccepted or MeetingMessageType.MeetingDeclined;
        var odataType = type == MeetingMessageType.MeetingCancelled
            ? EventMessageDto.EventMessageODataType
            : isResponse ? EventMessageDto.ResponseODataType : EventMessageDto.RequestODataType;
        var prefer = options.Prefer;

        return new EventMessageDto(message)
        {
            ODataType = odataType,
            MeetingMessageType = ToGraph(type),
            StartDateTime = meeting?.Start is { } start ? ToDateTimeTimeZone(start, prefer) : null,
            EndDateTime = meeting?.End is { } end ? ToDateTimeTimeZone(end, prefer) : null,
            Location = meeting?.Location is { } location ? new LocationDto { DisplayName = location } : null,
            IsAllDay = meeting?.IsAllDay ?? false,
            ResponseType = isResponse ? ResponseTypeFor(type) : null,
            ProposedNewTime = isResponse && meeting is { ProposedStart: { } ps, ProposedEnd: { } pe }
                ? new TimeSlotDto { Start = ToDateTimeTimeZone(ps, prefer), End = ToDateTimeTimeZone(pe, prefer) }
                : null,
        };
    }

    private static string ResponseTypeFor(MeetingMessageType type) => type switch
    {
        MeetingMessageType.MeetingTentativelyAccepted => "tentativelyAccepted",
        MeetingMessageType.MeetingDeclined => "declined",
        _ => "accepted",
    };

    private static RecipientDto? ToRecipient(EmailAddress? address) => address is null ? null : ToRecipientDto(address);
}
