using System.Text;
using System.Text.Json;

using Nightware.Adapters.Dav.Chats.Talk;
using Nightware.Adapters.Dav.Teams;
using Nightware.Application.Chats;
using Nightware.Application.Subscriptions;
using Nightware.Domain.Kernel;
using Nightware.Domain.Subscriptions;

namespace Nightware.Adapters.Dav.Chats;

/// <summary>
/// <see cref="ITeamsChangeFeed"/> on Talk (ADR 0009 D11), as the subscriber. Messages follow each room's chat after the
/// last message id seen (Talk's ids only grow, so a message past the cursor is <c>created</c>); an edit, reaction or
/// delete is Talk's system message about its parent, reported as <c>updated</c> or <c>deleted</c>. Reads never move the
/// subscriber's read marker or clear their notifications. Chats and members are compared with the state's snapshot.
/// </summary>
internal sealed class TalkChangeFeed(
    TalkChatClient talk, TalkChatRooms chatRooms, TalkChannelRooms channelRooms, TalkChannelStore channels, IChatMembers members, TimeProvider time) : ITeamsChangeFeed
{
    internal const string MessageType = "#Microsoft.Graph.chatMessage";
    internal const string ChatType = "#microsoft.graph.chat";
    internal const string MemberType = "#microsoft.graph.aadUserConversationMember";

    /// <summary>Messages read from one room per poll (pages of <see cref="RoundSize"/>); the rest wait for the next poll.</summary>
    internal const int Rounds = 5;

    internal const int RoundSize = 100;

    /// <summary>How far back a chat that has just appeared is read for messages sent since the previous poll.</summary>
    internal const int NewRoomHistory = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Result<string>> StartAsync(SubscriptionTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        switch (target.Kind)
        {
            case SubscriptionResourceKind.ChatMessages:
            {
                var room = await ChatAsync(target.Folder!, cancellationToken).ConfigureAwait(false);
                return room.IsSuccess ? await CursorStateAsync(room.Value.Token, cancellationToken).ConfigureAwait(false) : room.Error;
            }

            case SubscriptionResourceKind.ChannelMessages:
            {
                var (team, channel) = Channel(target);
                var conversation = await channels.ConversationAsync(team, channel, cancellationToken).ConfigureAwait(false);
                return conversation.IsSuccess ? await CursorStateAsync(channel, cancellationToken).ConfigureAwait(false) : conversation.Error;
            }

            case SubscriptionResourceKind.AllChatMessages:
            {
                var chats = await ChatRoomsAsync(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
                if (!chats.IsSuccess)
                {
                    return chats.Error;
                }

                var cursors = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var room in chats.Value.Rooms)
                {
                    var newest = await NewestAsync(room.Token, cancellationToken).ConfigureAwait(false);
                    if (!newest.IsSuccess)
                    {
                        return newest.Error;
                    }

                    cursors[room.Token] = newest.Value;
                }

                return Encode(new AllChatsState(cursors, [.. chats.Value.Channels], Now()));
            }

            case SubscriptionResourceKind.Chats:
            {
                var chats = await ChatRoomsAsync(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
                return chats.IsSuccess ? Encode(new ChatsState(chats.Value.Rooms.ToDictionary(r => r.Token, Topic, StringComparer.Ordinal), [.. chats.Value.Channels])) : chats.Error;
            }

            case SubscriptionResourceKind.Chat:
            {
                var room = await ChatAsync(target.Folder!, cancellationToken).ConfigureAwait(false);
                return room.IsSuccess ? Encode(new TopicState(Topic(room.Value))) : room.Error;
            }

            case SubscriptionResourceKind.ChatMembers:
            {
                var room = await ChatAsync(target.Folder!, cancellationToken).ConfigureAwait(false);
                if (!room.IsSuccess)
                {
                    return room.Error;
                }

                var listed = await members.ListAsync(target.Folder!, cancellationToken).ConfigureAwait(false);
                return listed.IsSuccess ? Encode(new MembersState([.. listed.Value.Select(m => m.Id)])) : listed.Error;
            }

            default:
                return Error.Unsupported("Not a Teams resource.");
        }
    }

    public async Task<Result<ChangeBatch>> ChangesAsync(SubscriptionTarget target, string state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(state);
        switch (target.Kind)
        {
            case SubscriptionResourceKind.ChatMessages when Decode<CursorState>(state) is { } cursor:
            {
                var chatId = target.Folder!;
                var read = await RoomAsync(chatId, cursor.Cursor, m => ChatMessagePath(chatId, m.Id), cancellationToken).ConfigureAwait(false);
                return read.IsSuccess ? new ChangeBatch(read.Value.Changes, Encode(new CursorState(read.Value.Cursor)), Classified: true) : read.Error;
            }

            case SubscriptionResourceKind.ChannelMessages when Decode<CursorState>(state) is { } cursor:
            {
                var (team, channel) = Channel(target);
                var read = await RoomAsync(channel, cursor.Cursor, m => ChannelMessagePath(team, channel, m), cancellationToken).ConfigureAwait(false);
                return read.IsSuccess ? new ChangeBatch(read.Value.Changes, Encode(new CursorState(read.Value.Cursor)), Classified: true) : read.Error;
            }

            case SubscriptionResourceKind.AllChatMessages when Decode<AllChatsState>(state) is { } all:
                return await AllMessagesAsync(all, cancellationToken).ConfigureAwait(false);

            case SubscriptionResourceKind.Chats when Decode<ChatsState>(state) is { } known:
            {
                var chats = await ChatRoomsAsync(known.Channels.ToHashSet(StringComparer.Ordinal), known.Topics.Keys.ToHashSet(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
                if (!chats.IsSuccess)
                {
                    return chats.Error;
                }

                var changes = new List<ResourceChange>();
                foreach (var room in chats.Value.Rooms)
                {
                    if (!known.Topics.TryGetValue(room.Token, out var topic))
                    {
                        changes.Add(ChatChange(room.Token, SubscriptionChangeType.Created));
                    }
                    else if (topic != Topic(room))
                    {
                        changes.Add(ChatChange(room.Token, SubscriptionChangeType.Updated));
                    }
                }

                var next = new ChatsState(chats.Value.Rooms.ToDictionary(r => r.Token, Topic, StringComparer.Ordinal), [.. chats.Value.Channels]);
                return new ChangeBatch(changes, Encode(next), Classified: true);
            }

            case SubscriptionResourceKind.Chat when Decode<TopicState>(state) is { } known:
            {
                var room = await chatRooms.FindAsync(target.Folder!, cancellationToken).ConfigureAwait(false);
                if (!room.IsSuccess)
                {
                    return room.Error;
                }

                var topic = Topic(room.Value);
                ResourceChange[] changes = topic == known.Topic ? [] : [ChatChange(room.Value.Token, SubscriptionChangeType.Updated)];
                return new ChangeBatch(changes, Encode(new TopicState(topic)), Classified: true);
            }

            case SubscriptionResourceKind.ChatMembers when Decode<MembersState>(state) is { } known:
            {
                var chatId = target.Folder!;
                var listed = await members.ListAsync(chatId, cancellationToken).ConfigureAwait(false);
                if (!listed.IsSuccess)
                {
                    return listed.Error;
                }

                var now = listed.Value.Select(m => m.Id).ToList();
                var before = known.Ids.ToHashSet(StringComparer.Ordinal);
                var after = now.ToHashSet(StringComparer.Ordinal);
                var changes = now.Where(id => !before.Contains(id)).Select(id => MemberChange(chatId, id, SubscriptionChangeType.Created))
                    .Concat(known.Ids.Where(id => !after.Contains(id)).Select(id => MemberChange(chatId, id, SubscriptionChangeType.Deleted)))
                    .ToList();
                return new ChangeBatch(changes, Encode(new MembersState(now)), Classified: true);
            }

            default:
                return Error.SyncStateGone("The subscription's state is not valid for its resource; it starts again from now.");
        }
    }

    internal static string ChatMessagePath(string chatId, long id) => $"chats('{chatId}')/messages('{TalkIds.Decimal(id)}')";

    /// <summary>A post as <c>messages('{id}')</c>, a reply under its post as <c>messages('{post}')/replies('{id}')</c>.</summary>
    internal static string ChannelMessagePath(string teamId, string channelId, TalkMessage message)
    {
        var channel = $"teams('{teamId}')/channels('{channelId}')/messages";
        return TalkMessageCodec.IsThreadReply(message)
            ? $"{channel}('{TalkIds.Decimal(message.ThreadId)}')/replies('{TalkIds.Decimal(message.Id)}')"
            : $"{channel}('{TalkIds.Decimal(message.Id)}')";
    }

    /// <summary>
    /// What happened in one room after <paramref name="after"/>: each new listed message is created; an edit, reaction or
    /// delete names its parent (updated, or deleted), unless the parent is itself new in this read.
    /// </summary>
    internal async Task<Result<(List<ResourceChange> Changes, long Cursor)>> RoomAsync(
        string token, long after, Func<TalkMessage, string> path, CancellationToken cancellationToken)
    {
        var changes = new List<ResourceChange>();
        var updated = new HashSet<long>();
        var cursor = after;
        for (var round = 0; round < Rounds; round++)
        {
            var page = await talk.FutureAsync(token, cursor, RoundSize, cancellationToken).ConfigureAwait(false);
            if (!page.IsSuccess)
            {
                return page.Error;
            }

            foreach (var message in page.Value.Messages)
            {
                if (TalkMessageCodec.IsListed(message))
                {
                    changes.Add(MessageChange(message, SubscriptionChangeType.Created, path));
                }
                else if (message.IsSystem && TalkMessageCodec.ChangesToParent.Contains(message.SystemMessage)
                         && message.Parent is { } parent && TalkMessageCodec.IsListed(parent))
                {
                    if (message.SystemMessage == TalkMessageCodec.MessageDeleted)
                    {
                        changes.Add(MessageChange(parent, SubscriptionChangeType.Deleted, path));
                    }
                    else if (parent.Id <= after && updated.Add(parent.Id))
                    {
                        changes.Add(MessageChange(parent, SubscriptionChangeType.Updated, path));
                    }
                }
            }

            if (page.Value.LastGiven is not { } lastGiven)
            {
                break;
            }

            cursor = Math.Max(cursor, lastGiven);
            if (page.Value.Messages.Count < RoundSize)
            {
                break;
            }
        }

        return (changes, cursor);
    }

    private async Task<Result<ChangeBatch>> AllMessagesAsync(AllChatsState state, CancellationToken cancellationToken)
    {
        var now = Now();
        var chats = await ChatRoomsAsync(state.Channels.ToHashSet(StringComparer.Ordinal), state.Rooms.Keys.ToHashSet(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
        if (!chats.IsSuccess)
        {
            return chats.Error;
        }

        var changes = new List<ResourceChange>();
        var cursors = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var room in chats.Value.Rooms)
        {
            var token = room.Token;
            if (state.Rooms.TryGetValue(token, out var after))
            {
                var read = await RoomAsync(token, after, m => ChatMessagePath(token, m.Id), cancellationToken).ConfigureAwait(false);
                if (!read.IsSuccess)
                {
                    // A chat left since the room list was read is dropped; anything else fails the poll.
                    if (read.Error.Kind == ErrorKind.NotFound)
                    {
                        continue;
                    }

                    return read.Error;
                }

                changes.AddRange(read.Value.Changes);
                cursors[token] = read.Value.Cursor;
                continue;
            }

            // A chat that has appeared (made, or the subscriber added): its messages since the previous poll are new.
            var history = await talk.HistoryAsync(token, null, NewRoomHistory, null, cancellationToken).ConfigureAwait(false);
            if (!history.IsSuccess)
            {
                if (history.Error.Kind == ErrorKind.NotFound)
                {
                    continue;
                }

                return history.Error;
            }

            var since = DateTimeOffset.FromUnixTimeSeconds(state.At);
            changes.AddRange(history.Value.Messages
                .Where(m => TalkMessageCodec.IsListed(m) && m.Timestamp >= since)
                .OrderBy(m => m.Id)
                .Select(m => MessageChange(m, SubscriptionChangeType.Created, x => ChatMessagePath(token, x.Id))));
            cursors[token] = history.Value.Messages.Count > 0 ? history.Value.Messages.Max(m => m.Id) : 0;
        }

        return new ChangeBatch(changes, Encode(new AllChatsState(cursors, [.. chats.Value.Channels], now)), Classified: true);
    }

    /// <summary>
    /// The subscriber's chats (D3: channels, the note to self and samples left out). The teams' channel rooms are listed
    /// again only when a group room appears that is neither a known channel nor a known chat.
    /// </summary>
    private async Task<Result<(List<TalkRoom> Rooms, IReadOnlySet<string> Channels)>> ChatRoomsAsync(
        HashSet<string> knownChannels, HashSet<string> knownChats, CancellationToken cancellationToken)
    {
        var listed = await talk.ListRoomsAsync(cancellationToken).ConfigureAwait(false);
        if (!listed.IsSuccess)
        {
            return listed.Error;
        }

        var chats = listed.Value.Where(TalkChatRooms.IsChat).ToList();
        IReadOnlySet<string> channelTokens = knownChannels;
        if (chats.Any(r => !r.IsOneToOne && !knownChannels.Contains(r.Token) && !knownChats.Contains(r.Token)))
        {
            channelTokens = await channelRooms.TokensAsync(cancellationToken).ConfigureAwait(false);
        }

        return (chats.Where(r => !channelTokens.Contains(r.Token)).ToList(), channelTokens);
    }

    /// <summary>One of the subscriber's chats; NotFound for a channel's room or anything else that isn't a chat of theirs.</summary>
    private async Task<Result<TalkRoom>> ChatAsync(string chatId, CancellationToken cancellationToken)
    {
        var room = await chatRooms.FindAsync(chatId, cancellationToken).ConfigureAwait(false);
        if (!room.IsSuccess)
        {
            return room.Error;
        }

        if (!room.Value.IsOneToOne && (await channelRooms.TokensAsync(cancellationToken).ConfigureAwait(false)).Contains(room.Value.Token))
        {
            return TalkChatClient.NoConversation();
        }

        return room;
    }

    private async Task<Result<string>> CursorStateAsync(string token, CancellationToken cancellationToken)
    {
        var newest = await NewestAsync(token, cancellationToken).ConfigureAwait(false);
        return newest.IsSuccess ? Encode(new CursorState(newest.Value)) : newest.Error;
    }

    /// <summary>The room's newest message id, Talk's bookkeeping included (an edit is a message too).</summary>
    private async Task<Result<long>> NewestAsync(string token, CancellationToken cancellationToken)
    {
        var newest = await talk.HistoryAsync(token, null, 1, null, cancellationToken).ConfigureAwait(false);
        return newest.IsSuccess ? newest.Value.Messages.Count > 0 ? newest.Value.Messages.Max(m => m.Id) : 0 : newest.Error;
    }

    private static (string Team, string Channel) Channel(SubscriptionTarget target)
    {
        var parts = (target.Folder ?? string.Empty).Split('/', 2);
        return (parts[0], parts.Length > 1 ? parts[1] : string.Empty);
    }

    /// <summary>What a chat subscription compares: a group chat's name (Graph's <c>topic</c>); a one-on-one chat has none.</summary>
    private static string Topic(TalkRoom room) => room.IsOneToOne ? string.Empty : room.Name;

    private static ResourceChange MessageChange(TalkMessage message, SubscriptionChangeType type, Func<TalkMessage, string> path) =>
        new(TalkIds.Decimal(message.Id), type) { Resource = path(message), ODataType = MessageType };

    private static ResourceChange ChatChange(string token, SubscriptionChangeType type) =>
        new(token, type) { Resource = $"chats('{token}')", ODataType = ChatType };

    private static ResourceChange MemberChange(string chatId, string membershipId, SubscriptionChangeType type) =>
        new(membershipId, type) { Resource = $"chats('{chatId}')/members('{membershipId}')", ODataType = MemberType };

    private long Now() => time.GetUtcNow().ToUnixTimeSeconds();

    private static string Encode<T>(T state) => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state, Json)));

    private static T? Decode<T>(string state)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(Encoding.UTF8.GetString(Convert.FromBase64String(state)), Json);
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    internal sealed record CursorState(long Cursor);

    /// <param name="At">When the rooms were last read (unix seconds): a chat that appears later is read from then.</param>
    internal sealed record AllChatsState(Dictionary<string, long> Rooms, List<string> Channels, long At);

    internal sealed record ChatsState(Dictionary<string, string> Topics, List<string> Channels);

    internal sealed record TopicState(string Topic);

    internal sealed record MembersState(List<string> Ids);
}
