namespace Nightware.Domain.Chats;

/// <summary>Graph's <c>chatMessageType</c>: a person's message or a system event (members added, renamed, call ended).</summary>
public enum ChatMessageType
{
    Message,
    SystemEventMessage,
}

/// <summary>Graph's <c>bodyType</c>.</summary>
public enum ChatBodyType
{
    Text,
    Html,
}

public sealed record ChatMessageBody(ChatBodyType ContentType, string Content)
{
    /// <summary>The body Graph gives a system event message.</summary>
    public static ChatMessageBody SystemEvent { get; } = new(ChatBodyType.Html, "<systemEventMessage/>");

    /// <summary>A deleted message's body: empty, as Graph's.</summary>
    public static ChatMessageBody Deleted { get; } = new(ChatBodyType.Html, string.Empty);
}

/// <summary>Who an identity is: a person (in the directory or not), an anonymous guest, or the system/a bot.</summary>
public enum ChatIdentityKind
{
    User,
    Guest,
    Application,
}

/// <summary>
/// Someone in a conversation (Graph <c>identitySet</c> / <c>teamworkUserIdentity</c>). <see cref="Id"/> is the directory id,
/// null for anyone the directory doesn't know (a guest, a bot, a federated user), who keeps only a display name
/// (ADR 0009 D2).
/// </summary>
public sealed record ChatIdentity(string? Id, string? DisplayName, ChatIdentityKind Kind = ChatIdentityKind.User);

/// <summary>A channel message's place (Graph <c>channelIdentity</c>); a chat message has <see cref="ChatMessage.ChatId"/> instead.</summary>
public sealed record ChannelIdentity(string TeamId, string ChannelId);

/// <summary>
/// A message in a chat or a channel (Graph <c>chatMessage</c>, ADR 0009 D4). Graph uses one type for both: a chat message
/// carries <see cref="ChatId"/>, a channel message <see cref="ChannelIdentity"/>, and a channel reply
/// <see cref="ReplyToId"/> naming its post.
/// </summary>
/// <param name="Id">The backend's message id in decimal; unique within its conversation.</param>
public sealed record ChatMessage(string Id, ChatMessageType MessageType, DateTimeOffset CreatedDateTime)
{
    public string? ReplyToId { get; init; }

    public DateTimeOffset? LastModifiedDateTime { get; init; }

    public DateTimeOffset? LastEditedDateTime { get; init; }

    public DateTimeOffset? DeletedDateTime { get; init; }

    /// <summary>A channel post's subject; null in chats.</summary>
    public string? Subject { get; init; }

    public string? ChatId { get; init; }

    public ChannelIdentity? ChannelIdentity { get; init; }

    /// <summary>The sender; null for a system event.</summary>
    public ChatIdentity? From { get; init; }

    public ChatMessageBody Body { get; init; } = new(ChatBodyType.Html, string.Empty);

    public IReadOnlyList<ChatMessageAttachment> Attachments { get; init; } = [];

    public IReadOnlyList<ChatMention> Mentions { get; init; } = [];

    public IReadOnlyList<ChatReaction> Reactions { get; init; } = [];

    /// <summary>Graph's <c>importance</c>; the backend has none, so it is always <c>normal</c>.</summary>
    public string Importance { get; init; } = "normal";

    public ChatEventDetail? EventDetail { get; init; }

    public string? WebUrl { get; init; }

    /// <summary>Changes with every edit or delete (Graph <c>etag</c>).</summary>
    public string ETag => (LastModifiedDateTime ?? CreatedDateTime).ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// A file shared into the conversation (Graph <c>chatMessageAttachment</c> with <c>contentType: reference</c>). The id is
/// the file's id, which addresses it as a drive item (ADR 0008 D2).
/// </summary>
public sealed record ChatMessageAttachment(string Id, string ContentType, string? ContentUrl, string? Name)
{
    public const string Reference = "reference";
}

/// <summary>A mention (Graph <c>chatMessageMention</c>); the body carries <c>&lt;at id="{Id}"&gt;</c> where it appears.</summary>
public sealed record ChatMention(int Id, string MentionText, ChatMentioned Mentioned);

/// <summary>Who or what a mention names: a person, or the whole conversation (<c>@all</c>).</summary>
public abstract record ChatMentioned
{
    private ChatMentioned()
    {
    }

    public sealed record User(ChatIdentity Identity) : ChatMentioned;

    /// <param name="ConversationType">Graph's <c>conversationIdentityType</c>: <c>chat</c> or <c>channel</c>.</param>
    public sealed record Conversation(string Id, string? DisplayName, string ConversationType) : ChatMentioned;
}

/// <summary>A reaction (Graph <c>chatMessageReaction</c>): an emoji, who and when.</summary>
public sealed record ChatReaction(string ReactionType, DateTimeOffset CreatedDateTime, ChatIdentity User);

/// <summary>What a system event message reports (Graph's <c>eventMessageDetail</c> subtypes).</summary>
public abstract record ChatEventDetail(ChatIdentity? Initiator)
{
    public sealed record MembersAdded(ChatIdentity? Initiator, IReadOnlyList<ChatIdentity> Members) : ChatEventDetail(Initiator);

    public sealed record MembersDeleted(ChatIdentity? Initiator, IReadOnlyList<ChatIdentity> Members) : ChatEventDetail(Initiator);

    /// <param name="ChatDisplayName">The new name; null where the backend didn't say.</param>
    public sealed record ChatRenamed(ChatIdentity? Initiator, string ChatId, string? ChatDisplayName) : ChatEventDetail(Initiator);

    /// <param name="Duration">Null where the backend didn't say.</param>
    public sealed record CallEnded(ChatIdentity? Initiator, TimeSpan? Duration, IReadOnlyList<ChatIdentity> Participants) : ChatEventDetail(Initiator);
}

/// <summary>A message to send, or a new body for one (Graph <c>POST …/messages</c>, <c>PATCH</c>).</summary>
public sealed record ChatMessageDraft(ChatMessageBody Body)
{
    public IReadOnlyList<ChatMentionDraft> Mentions { get; init; } = [];

    /// <summary>A channel post's subject; chats have none.</summary>
    public string? Subject { get; init; }
}

/// <summary>
/// A mention in a sent html body: <c>&lt;at id="{Id}"&gt;</c> names <see cref="User"/> (a directory id) or, with
/// <see cref="IsConversation"/>, everyone in the conversation.
/// </summary>
public sealed record ChatMentionDraft(int Id, string? MentionText, string? User, bool IsConversation = false);
