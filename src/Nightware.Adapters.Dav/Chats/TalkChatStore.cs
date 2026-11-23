using System.Globalization;

using Nightware.Adapters.Dav.Chats.Talk;
using Nightware.Application.Chats;
using Nightware.Domain.Chats;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Chats;

/// <summary>
/// <see cref="IChatStore"/> on Talk conversations (ADR 0009 D3), as the caller. Talk lists all of a user's rooms in one
/// answer, so a page is a slice of that list (most recently active first) and its skip token the offset.
/// </summary>
internal sealed class TalkChatStore(TalkChatClient talk, TalkChatRooms rooms, TalkChannelRooms channels, TalkMessageFeed feed, TalkIdentities identities) : IChatStore
{
    /// <summary>Talk's limit on a room name.</summary>
    internal const int MaxRoomName = 255;

    public async Task<Result<Page<Chat>>> ListAsync(ChatPageQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var offset = 0;
        if (query.SkipToken is not null
            && (!int.TryParse(query.SkipToken, NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0))
        {
            return Error.Validation("The $skiptoken is not valid.", "$skiptoken");
        }

        var listed = await talk.ListRoomsAsync(cancellationToken).ConfigureAwait(false);
        if (!listed.IsSuccess)
        {
            return listed.Error;
        }

        var candidates = listed.Value.Where(TalkChatRooms.IsChat).ToList();
        IReadOnlySet<string> channelTokens = candidates.Any(r => !r.IsOneToOne)
            ? await channels.TokensAsync(cancellationToken).ConfigureAwait(false)
            : new HashSet<string>();
        var chats = candidates
            .Where(r => !channelTokens.Contains(r.Token))
            .OrderByDescending(r => r.LastActivity ?? DateTimeOffset.MinValue)
            .ThenByDescending(r => r.Id)
            .ToList();

        var page = new List<Chat>();
        foreach (var room in chats.Skip(offset).Take(query.Top))
        {
            ChatMessage? last = null;
            if (query.WithLastMessage && room.LastMessage is { } message && TalkMessageCodec.IsListed(message))
            {
                // A preview carries no reactions (Graph's chatMessageInfo has none), so who reacted isn't read.
                var decoded = await feed.DecodeOneAsync(rooms.Conversation(room), message with { ReactionCounts = new Dictionary<string, int>() }, cancellationToken)
                    .ConfigureAwait(false);
                if (!decoded.IsSuccess)
                {
                    return decoded.Error;
                }

                last = decoded.Value;
            }

            page.Add(rooms.ToChat(room, last));
        }

        var next = offset + query.Top < chats.Count ? (offset + query.Top).ToString(CultureInfo.InvariantCulture) : null;
        return new Page<Chat>(page, next);
    }

    public async Task<Result<Chat>> GetAsync(string chatId, CancellationToken cancellationToken)
    {
        var room = await rooms.FindAsync(chatId, cancellationToken).ConfigureAwait(false);
        return room.IsSuccess ? rooms.ToChat(room.Value) : room.Error;
    }

    public async Task<Result<Chat>> CreateAsync(ChatCreation creation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(creation);
        var accountNames = new List<string>(creation.Members.Count);
        foreach (var member in creation.Members)
        {
            var accountName = await identities.AccountNameOfAsync(member.User, cancellationToken).ConfigureAwait(false);
            if (!accountName.IsSuccess)
            {
                return accountName.Error;
            }

            accountNames.Add(accountName.Value);
        }

        Result<TalkRoom> created;
        if (creation.ChatType == ChatType.OneOnOne)
        {
            created = await talk.CreateRoomAsync(TalkRoomType.OneToOne, null, accountNames, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var name = creation.Topic;
            if (string.IsNullOrEmpty(name))
            {
                // Talk needs a name; Teams shows an untitled group chat by its members' names.
                var loaded = await identities.LoadAsync(accountNames, cancellationToken).ConfigureAwait(false);
                if (!loaded.IsSuccess)
                {
                    return loaded.Error!;
                }

                name = string.Join(", ", accountNames.Select(a => identities.UserOf(a)?.DisplayName ?? a));
            }

            created = await talk.CreateRoomAsync(TalkRoomType.Group, name.Length <= MaxRoomName ? name : name[..MaxRoomName], accountNames, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!created.IsSuccess)
        {
            return created.Error;
        }

        var owners = creation.ChatType == ChatType.OneOnOne
            ? []
            : creation.Members.Select((m, i) => (m, i)).Where(x => x.m.IsOwner).Select(x => accountNames[x.i]).ToHashSet(StringComparer.Ordinal);
        if (owners.Count > 0)
        {
            var participants = await talk.ParticipantsAsync(created.Value.Token, cancellationToken).ConfigureAwait(false);
            if (!participants.IsSuccess)
            {
                return participants.Error;
            }

            foreach (var participant in participants.Value.Where(p => p.Actor.IsUser && owners.Contains(p.Actor.Id) && !TalkParticipantType.Moderates(p.ParticipantType)))
            {
                var promoted = await talk.PromoteAsync(created.Value.Token, participant.AttendeeId, cancellationToken).ConfigureAwait(false);
                if (!promoted.IsSuccess)
                {
                    return promoted.Error!;
                }
            }
        }

        return rooms.ToChat(created.Value);
    }

    public async Task<Result<Chat>> RenameAsync(string chatId, string topic, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(topic);
        var room = await rooms.FindAsync(chatId, cancellationToken).ConfigureAwait(false);
        if (!room.IsSuccess)
        {
            return room.Error;
        }

        if (room.Value.IsOneToOne)
        {
            return Error.Validation("A oneOnOne chat has no topic.", "topic");
        }

        var name = topic.Length <= MaxRoomName ? topic : topic[..MaxRoomName];
        var renamed = await talk.RenameRoomAsync(room.Value.Token, name, cancellationToken).ConfigureAwait(false);
        return renamed.IsSuccess ? rooms.ToChat(room.Value with { Name = name, DisplayName = name }) : renamed.Error!;
    }

    public Task<Result> SetHiddenAsync(string chatId, bool hidden, CancellationToken cancellationToken) =>
        talk.SetArchivedAsync(chatId, hidden, cancellationToken);

    public Task<Result> MarkReadAsync(string chatId, CancellationToken cancellationToken) => talk.MarkReadAsync(chatId, cancellationToken);

    public Task<Result> MarkUnreadAsync(string chatId, CancellationToken cancellationToken) => talk.MarkUnreadAsync(chatId, cancellationToken);
}
