using Nightware.Adapters.Dav.Chats.Talk;
using Nightware.Domain.Chats;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Chats;

/// <summary>
/// Talk rooms as Graph chats (ADR 0009 D3): which rooms are chats, their type and topic, and the conversation the
/// message codec reads them as.
/// </summary>
internal sealed class TalkChatRooms(TalkChatClient talk, TalkLinks links)
{
    /// <summary>
    /// One-to-one, group and public rooms are chats; Talk's changelog, note to self and sample conversation are not.
    /// A channel's room is left out of listings by the caller (<see cref="TalkChannelRooms"/>).
    /// </summary>
    public static bool IsChat(TalkRoom room)
    {
        ArgumentNullException.ThrowIfNull(room);
        return room.Type is TalkRoomType.OneToOne or TalkRoomType.FormerOneToOne or TalkRoomType.Group or TalkRoomType.Public
               && room.ObjectType is not ("sample" or "note_to_self");
    }

    public static ChatType TypeOf(TalkRoom room)
    {
        ArgumentNullException.ThrowIfNull(room);
        return room.IsOneToOne ? ChatType.OneOnOne : room.ObjectType == "event" ? ChatType.Meeting : ChatType.Group;
    }

    /// <summary>The caller's chat by id; NotFound for a room that isn't a chat or isn't theirs.</summary>
    public async Task<Result<TalkRoom>> FindAsync(string chatId, CancellationToken cancellationToken)
    {
        var room = await talk.GetRoomAsync(chatId, cancellationToken).ConfigureAwait(false);
        return room.IsSuccess && !IsChat(room.Value) ? TalkChatClient.NoConversation() : room;
    }

    public TalkConversation Conversation(TalkRoom room)
    {
        ArgumentNullException.ThrowIfNull(room);
        return new TalkConversation(room.Token, room.IsOneToOne ? room.DisplayName : room.Name) { WebUrl = links.ConversationUrl(room.Token) };
    }

    /// <param name="lastMessage">The newest message, decoded, for <c>lastMessagePreview</c>; null to leave it out.</param>
    public Chat ToChat(TalkRoom room, ChatMessage? lastMessage = null)
    {
        ArgumentNullException.ThrowIfNull(room);
        var type = TypeOf(room);
        var url = links.ConversationUrl(room.Token);

        // Talk keeps the id of the last message read, not its time: it is only known when that is the newest message.
        // (The unread count can't say: it leaves out the caller's own messages, so it is 0 after "mark unread" on one.)
        var read = room.LastMessage is { } last && room.LastReadMessage >= last.Id ? last.Timestamp : (DateTimeOffset?)null;
        return new Chat(room.Token, type)
        {
            Topic = type == ChatType.OneOnOne ? null : room.Name,
            LastUpdatedDateTime = room.LastActivity ?? room.LastMessage?.Timestamp,
            WebUrl = url,
            Viewpoint = new ChatViewpoint(room.IsArchived, read),
            JoinWebUrl = type == ChatType.Meeting ? url : null,
            LastMessage = lastMessage,

            // Talk names no last message, or only the one it made the room with. A newest message that is Talk's
            // bookkeeping (a reaction, an edit, a deletion) says nothing about the comments before it.
            NeverWritten = room.LastMessage is not { } newest || newest.SystemMessage == TalkMessageCodec.ConversationCreated,
        };
    }
}
