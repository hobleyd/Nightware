namespace Nightware.Domain.Chats;

/// <summary>Graph's <c>chatType</c>.</summary>
public enum ChatType
{
    OneOnOne,
    Group,
    Meeting,
}

/// <summary>
/// A chat (Graph <c>chat</c>, ADR 0009 D3): a conversation of the chat backend. Its id is the backend's own
/// (a Talk room token), so it needs no mapping.
/// </summary>
public sealed record Chat(string Id, ChatType ChatType)
{
    /// <summary>The group's or meeting's name; null for a one-to-one chat, which has none.</summary>
    public string? Topic { get; init; }

    /// <summary>Null where the backend keeps no creation time.</summary>
    public DateTimeOffset? CreatedDateTime { get; init; }

    public DateTimeOffset? LastUpdatedDateTime { get; init; }

    public string? WebUrl { get; init; }

    /// <summary>The caller's view of the chat (Graph <c>viewpoint</c>).</summary>
    public ChatViewpoint Viewpoint { get; init; } = new(false, null);

    /// <summary>A meeting chat's join link (Graph <c>onlineMeetingInfo.joinWebUrl</c>); null otherwise.</summary>
    public string? JoinWebUrl { get; init; }

    /// <summary>The newest message (Graph's <c>lastMessagePreview</c>), when the backend had one to hand.</summary>
    public ChatMessage? LastMessage { get; init; }

    /// <summary>
    /// Nobody has written in it — Graph's <c>lastMessagePreview</c> is then <c>null</c> rather than left out. False
    /// where the backend can't tell, which a missing <see cref="LastMessage"/> alone doesn't.
    /// </summary>
    public bool NeverWritten { get; init; }
}

/// <param name="IsHidden">The caller hid (archived) the chat.</param>
/// <param name="LastMessageReadDateTime">When the newest message the caller read was sent; null where unknown.</param>
public sealed record ChatViewpoint(bool IsHidden, DateTimeOffset? LastMessageReadDateTime);

/// <summary>
/// A member of a chat (Graph <c>aadUserConversationMember</c>). <see cref="UserId"/> is the directory id; it is null for
/// a member the directory doesn't know.
/// </summary>
/// <param name="Id">The membership id (ADR 0009 D2: <c>m.</c> plus the backend's attendee id).</param>
public sealed record ChatMember(string Id, string? DisplayName)
{
    public string? UserId { get; init; }

    public string? Email { get; init; }

    /// <summary><c>["owner"]</c> for an owner or moderator, otherwise empty.</summary>
    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>History is always visible to every member: <see cref="DateTimeOffset.MinValue"/>, as Graph writes "everything".</summary>
    public DateTimeOffset VisibleHistoryStartDateTime { get; init; } = DateTimeOffset.MinValue;

    public const string OwnerRole = "owner";
}

/// <summary>A user to add to a chat, by directory id (or UPN), optionally as an owner.</summary>
public sealed record ChatMemberDraft(string User, bool IsOwner);

/// <summary>A new chat (Graph <c>POST /chats</c>): its type, topic and members other than the caller.</summary>
public sealed record ChatCreation(ChatType ChatType, string? Topic, IReadOnlyList<ChatMemberDraft> Members);

/// <summary>A pinned message (Graph <c>pinnedChatMessageInfo</c>). Its id is the message's.</summary>
public sealed record PinnedChatMessage(string Id, ChatMessage Message);
