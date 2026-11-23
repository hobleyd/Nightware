using System.Text.Json.Serialization;
using System.Xml;

using Nightware.Domain.Chats;

namespace Nightware.Api.Features.Chats.Dto;

/// <summary>Graph <c>chat</c> (ADR 0009 D3). <c>tenantId</c> is left out: there is no tenant.</summary>
public sealed record ChatDto
{
    public required string Id { get; init; }

    public string? Topic { get; init; }

    public DateTimeOffset? CreatedDateTime { get; init; }

    public DateTimeOffset? LastUpdatedDateTime { get; init; }

    public required string ChatType { get; init; }

    public string? WebUrl { get; init; }

    public required ChatViewpointDto Viewpoint { get; init; }

    public TeamworkOnlineMeetingInfoDto? OnlineMeetingInfo { get; init; }

    /// <summary>Talk can't hide a chat from everyone, only for oneself.</summary>
    public bool IsHiddenForAllMembers { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ConversationMemberDto>? Members { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ChatMessageInfoDto? LastMessagePreview { get; init; }

    public static ChatDto From(Chat chat, IReadOnlyList<ChatMember>? members = null, bool withLastMessage = false)
    {
        ArgumentNullException.ThrowIfNull(chat);
        return new ChatDto
        {
            Id = chat.Id,
            Topic = chat.Topic,
            CreatedDateTime = chat.CreatedDateTime,
            LastUpdatedDateTime = chat.LastUpdatedDateTime,
            ChatType = ChatTypeName(chat.ChatType),
            WebUrl = chat.WebUrl,
            Viewpoint = new ChatViewpointDto { IsHidden = chat.Viewpoint.IsHidden, LastMessageReadDateTime = chat.Viewpoint.LastMessageReadDateTime },
            OnlineMeetingInfo = chat.JoinWebUrl is { } join ? new TeamworkOnlineMeetingInfoDto { JoinWebUrl = join } : null,
            Members = members?.Select(ConversationMemberDto.From).ToList(),
            LastMessagePreview = withLastMessage && chat.LastMessage is { } last ? ChatMessageInfoDto.Of(last) : null,
        };
    }

    public static string ChatTypeName(ChatType type) => type switch
    {
        Domain.Chats.ChatType.OneOnOne => "oneOnOne",
        Domain.Chats.ChatType.Meeting => "meeting",
        _ => "group",
    };
}

public sealed record ChatViewpointDto
{
    public bool IsHidden { get; init; }

    public DateTimeOffset? LastMessageReadDateTime { get; init; }
}

/// <summary>Graph <c>teamworkOnlineMeetingInfo</c>: a meeting chat's join link; the calendar event and organiser aren't known here.</summary>
public sealed record TeamworkOnlineMeetingInfoDto
{
    public string? CalendarEventId { get; init; }

    public required string JoinWebUrl { get; init; }

    public object? Organizer { get; init; }
}

/// <summary>Graph <c>aadUserConversationMember</c>.</summary>
public sealed record ConversationMemberDto
{
    public const string ODataTypeName = "#microsoft.graph.aadUserConversationMember";

    [JsonPropertyName("@odata.type")]
    [JsonPropertyOrder(-1)]
    public string ODataType { get; init; } = ODataTypeName;

    public required string Id { get; init; }

    public IReadOnlyList<string> Roles { get; init; } = [];

    public string? DisplayName { get; init; }

    public DateTimeOffset VisibleHistoryStartDateTime { get; init; }

    public string? UserId { get; init; }

    public string? Email { get; init; }

    public static ConversationMemberDto From(ChatMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return new ConversationMemberDto
        {
            Id = member.Id,
            Roles = member.Roles,
            DisplayName = member.DisplayName,
            VisibleHistoryStartDateTime = member.VisibleHistoryStartDateTime,
            UserId = member.UserId,
            Email = member.Email,
        };
    }
}

/// <summary>Graph <c>pinnedChatMessageInfo</c>; <c>message</c> only with <c>$expand=message</c>.</summary>
public sealed record PinnedChatMessageInfoDto
{
    [JsonPropertyName("@odata.type")]
    [JsonPropertyOrder(-1)]
    public string ODataType { get; init; } = "#microsoft.graph.pinnedChatMessageInfo";

    public required string Id { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ChatMessageDto? Message { get; init; }
}

/// <summary>Graph <c>chatMessageInfo</c>, a chat's <c>lastMessagePreview</c>.</summary>
public sealed record ChatMessageInfoDto
{
    public required string Id { get; init; }

    public DateTimeOffset CreatedDateTime { get; init; }

    public bool IsDeleted { get; init; }

    public required string MessageType { get; init; }

    public object? EventDetail { get; init; }

    public required ItemBodyDto Body { get; init; }

    public IdentitySetDto? From { get; init; }

    public static ChatMessageInfoDto Of(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new ChatMessageInfoDto
        {
            Id = message.Id,
            CreatedDateTime = message.CreatedDateTime,
            IsDeleted = message.DeletedDateTime is not null,
            MessageType = ChatMessageDto.TypeName(message.MessageType),
            EventDetail = EventDetailDtos.From(message.EventDetail),
            Body = ItemBodyDto.From(message.Body),
            From = IdentitySetDto.From(message.From),
        };
    }
}

/// <summary>
/// Graph <c>chatMessage</c> (ADR 0009 D4), for chats and channels alike. Fields Graph has and Nightware can't fill
/// (<c>summary</c>, <c>locale</c>, <c>policyViolation</c>, hosted contents) are left out.
/// </summary>
public sealed record ChatMessageDto
{
    public required string Id { get; init; }

    public string? ReplyToId { get; init; }

    public required string Etag { get; init; }

    public required string MessageType { get; init; }

    public DateTimeOffset CreatedDateTime { get; init; }

    public DateTimeOffset? LastModifiedDateTime { get; init; }

    public DateTimeOffset? LastEditedDateTime { get; init; }

    public DateTimeOffset? DeletedDateTime { get; init; }

    public string? Subject { get; init; }

    public string? ChatId { get; init; }

    public ChannelIdentityDto? ChannelIdentity { get; init; }

    public string Importance { get; init; } = "normal";

    public string? WebUrl { get; init; }

    public object? EventDetail { get; init; }

    public IdentitySetDto? From { get; init; }

    public required ItemBodyDto Body { get; init; }

    public IReadOnlyList<ChatMessageAttachmentDto> Attachments { get; init; } = [];

    public IReadOnlyList<ChatMessageMentionDto> Mentions { get; init; } = [];

    public IReadOnlyList<ChatMessageReactionDto> Reactions { get; init; } = [];

    public static ChatMessageDto Of(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new ChatMessageDto
        {
            Id = message.Id,
            ReplyToId = message.ReplyToId,
            Etag = message.ETag,
            MessageType = TypeName(message.MessageType),
            CreatedDateTime = message.CreatedDateTime,
            LastModifiedDateTime = message.LastModifiedDateTime ?? message.CreatedDateTime,
            LastEditedDateTime = message.LastEditedDateTime,
            DeletedDateTime = message.DeletedDateTime,
            Subject = message.Subject,
            ChatId = message.ChatId,
            ChannelIdentity = message.ChannelIdentity is { } channel ? new ChannelIdentityDto { TeamId = channel.TeamId, ChannelId = channel.ChannelId } : null,
            Importance = message.Importance,
            WebUrl = message.WebUrl,
            EventDetail = EventDetailDtos.From(message.EventDetail),
            From = IdentitySetDto.From(message.From),
            Body = ItemBodyDto.From(message.Body),
            Attachments = message.Attachments.Select(a => new ChatMessageAttachmentDto { Id = a.Id, ContentType = a.ContentType, ContentUrl = a.ContentUrl, Name = a.Name }).ToList(),
            Mentions = message.Mentions.Select(ChatMessageMentionDto.From).ToList(),
            Reactions = message.Reactions.Select(r => new ChatMessageReactionDto
            {
                ReactionType = r.ReactionType,
                CreatedDateTime = r.CreatedDateTime,
                User = IdentitySetDto.From(r.User)!,
            }).ToList(),
        };
    }

    public static string TypeName(ChatMessageType type) => type == ChatMessageType.SystemEventMessage ? "systemEventMessage" : "message";
}

public sealed record ChannelIdentityDto
{
    public required string TeamId { get; init; }

    public required string ChannelId { get; init; }
}

public sealed record ItemBodyDto
{
    public required string ContentType { get; init; }

    public required string Content { get; init; }

    public static ItemBodyDto From(ChatMessageBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return new ItemBodyDto { ContentType = body.ContentType == ChatBodyType.Text ? "text" : "html", Content = body.Content };
    }
}

/// <summary>Graph <c>chatMessageAttachment</c>: a file reference.</summary>
public sealed record ChatMessageAttachmentDto
{
    public required string Id { get; init; }

    public required string ContentType { get; init; }

    public string? ContentUrl { get; init; }

    public string? Content { get; init; }

    public string? Name { get; init; }

    public string? ThumbnailUrl { get; init; }
}

/// <summary>Graph <c>chatMessageMention</c>.</summary>
public sealed record ChatMessageMentionDto
{
    public int Id { get; init; }

    public required string MentionText { get; init; }

    public required ChatMessageMentionedIdentitySetDto Mentioned { get; init; }

    public static ChatMessageMentionDto From(ChatMention mention)
    {
        ArgumentNullException.ThrowIfNull(mention);
        return new ChatMessageMentionDto
        {
            Id = mention.Id,
            MentionText = mention.MentionText,
            Mentioned = mention.Mentioned switch
            {
                ChatMentioned.Conversation c => new ChatMessageMentionedIdentitySetDto
                {
                    Conversation = new TeamworkConversationIdentityDto { Id = c.Id, DisplayName = c.DisplayName, ConversationIdentityType = c.ConversationType },
                },
                ChatMentioned.User u => new ChatMessageMentionedIdentitySetDto { User = IdentityDto.User(u.Identity) },
                _ => new ChatMessageMentionedIdentitySetDto(),
            },
        };
    }
}

/// <summary>Graph <c>chatMessageMentionedIdentitySet</c>.</summary>
public sealed record ChatMessageMentionedIdentitySetDto
{
    public IdentityDto? Application { get; init; }

    public IdentityDto? Device { get; init; }

    public TeamworkConversationIdentityDto? Conversation { get; init; }

    public object? Tag { get; init; }

    public IdentityDto? User { get; init; }
}

public sealed record TeamworkConversationIdentityDto
{
    public required string Id { get; init; }

    public string? DisplayName { get; init; }

    public required string ConversationIdentityType { get; init; }
}

/// <summary>Graph <c>chatMessageReaction</c> (a unicode reaction: <c>reactionType</c> is the emoji).</summary>
public sealed record ChatMessageReactionDto
{
    public required string ReactionType { get; init; }

    /// <summary>The emoji's name, which Talk doesn't give.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Only for custom reactions, which Talk doesn't have.</summary>
    public string? ReactionContentUrl { get; init; }

    public DateTimeOffset CreatedDateTime { get; init; }

    public required IdentitySetDto User { get; init; }
}

/// <summary>Graph <c>identitySet</c> of a chat: a <c>user</c> (a person or a guest) or an <c>application</c> (Talk itself, a bot).</summary>
public sealed record IdentitySetDto
{
    public IdentityDto? Application { get; init; }

    public IdentityDto? Device { get; init; }

    public IdentityDto? User { get; init; }

    /// <param name="typed">Whether the identity names its <c>@odata.type</c>: Graph writes it on a message's sender and
    /// on reactions, not in an event's <c>initiator</c> or members.</param>
    public static IdentitySetDto? From(ChatIdentity? identity, bool typed = true) => identity switch
    {
        null => null,
        { Kind: ChatIdentityKind.Application } => new IdentitySetDto { Application = IdentityDto.Application(identity, typed) },
        _ => new IdentitySetDto { User = IdentityDto.User(identity, typed) },
    };
}

/// <summary>Graph <c>teamworkUserIdentity</c> or <c>teamworkApplicationIdentity</c>.</summary>
public sealed record IdentityDto
{
    [JsonPropertyName("@odata.type")]
    [JsonPropertyOrder(-1)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ODataType { get; init; }

    public string? Id { get; init; }

    public string? DisplayName { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UserIdentityType { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApplicationIdentityType { get; init; }

    public static IdentityDto User(ChatIdentity identity) => User(identity, typed: true);

    public static IdentityDto User(ChatIdentity identity, bool typed)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new IdentityDto
        {
            ODataType = typed ? "#microsoft.graph.teamworkUserIdentity" : null,
            Id = identity.Id,
            DisplayName = identity.DisplayName,
            UserIdentityType = identity.Kind == ChatIdentityKind.Guest ? "anonymousGuest" : "aadUser",
        };
    }

    public static IdentityDto Application(ChatIdentity identity, bool typed = true)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new IdentityDto
        {
            ODataType = typed ? "#microsoft.graph.teamworkApplicationIdentity" : null,
            Id = identity.Id,
            DisplayName = identity.DisplayName,
            ApplicationIdentityType = "bot",
        };
    }
}

/// <summary>Graph's <c>eventMessageDetail</c> subtypes, each written with its <c>@odata.type</c>.</summary>
public static class EventDetailDtos
{
    public static object? From(ChatEventDetail? detail) => detail switch
    {
        ChatEventDetail.MembersAdded added => new MembersAddedEventMessageDetailDto
        {
            Members = added.Members.Select(m => IdentityDto.User(m, typed: false)).ToList(),
            Initiator = IdentitySetDto.From(added.Initiator, typed: false),
        },
        ChatEventDetail.MembersDeleted deleted => new MembersDeletedEventMessageDetailDto
        {
            Members = deleted.Members.Select(m => IdentityDto.User(m, typed: false)).ToList(),
            Initiator = IdentitySetDto.From(deleted.Initiator, typed: false),
        },
        ChatEventDetail.ChatRenamed renamed => new ChatRenamedEventMessageDetailDto
        {
            ChatId = renamed.ChatId,
            ChatDisplayName = renamed.ChatDisplayName,
            Initiator = IdentitySetDto.From(renamed.Initiator, typed: false),
        },
        ChatEventDetail.CallEnded ended => new CallEndedEventMessageDetailDto
        {
            CallDuration = ended.Duration is { } duration ? XmlConvert.ToString(duration) : null,
            CallParticipants = ended.Participants.Select(p => new CallParticipantInfoDto { Participant = IdentitySetDto.From(p, typed: false) }).ToList(),
            Initiator = IdentitySetDto.From(ended.Initiator, typed: false),
        },
        _ => null,
    };
}

public sealed record MembersAddedEventMessageDetailDto
{
    [JsonPropertyName("@odata.type")]
    [JsonPropertyOrder(-1)]
    public string ODataType { get; init; } = "#microsoft.graph.membersAddedEventMessageDetail";

    public DateTimeOffset VisibleHistoryStartDateTime { get; init; } = DateTimeOffset.MinValue;

    public IReadOnlyList<IdentityDto> Members { get; init; } = [];

    public IdentitySetDto? Initiator { get; init; }
}

public sealed record MembersDeletedEventMessageDetailDto
{
    [JsonPropertyName("@odata.type")]
    [JsonPropertyOrder(-1)]
    public string ODataType { get; init; } = "#microsoft.graph.membersDeletedEventMessageDetail";

    public IReadOnlyList<IdentityDto> Members { get; init; } = [];

    public IdentitySetDto? Initiator { get; init; }
}

public sealed record ChatRenamedEventMessageDetailDto
{
    [JsonPropertyName("@odata.type")]
    [JsonPropertyOrder(-1)]
    public string ODataType { get; init; } = "#microsoft.graph.chatRenamedEventMessageDetail";

    public required string ChatId { get; init; }

    public string? ChatDisplayName { get; init; }

    public IdentitySetDto? Initiator { get; init; }
}

public sealed record CallEndedEventMessageDetailDto
{
    [JsonPropertyName("@odata.type")]
    [JsonPropertyOrder(-1)]
    public string ODataType { get; init; } = "#microsoft.graph.callEndedEventMessageDetail";

    public string? CallId { get; init; }

    /// <summary>ISO 8601 (<c>PT1M2S</c>); null where Talk didn't say.</summary>
    public string? CallDuration { get; init; }

    public string CallEventType { get; init; } = "call";

    public IReadOnlyList<CallParticipantInfoDto> CallParticipants { get; init; } = [];

    public IdentitySetDto? Initiator { get; init; }
}

public sealed record CallParticipantInfoDto
{
    public IdentitySetDto? Participant { get; init; }
}
