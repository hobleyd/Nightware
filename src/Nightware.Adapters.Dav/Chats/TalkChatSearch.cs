using Nightware.Adapters.Dav.Chats.Talk;
using Nightware.Application.Chats;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Chats;

/// <summary>
/// <see cref="IChatSearch"/> on Talk's message search (ADR 0009 D10), as the caller. Talk searches every conversation of
/// the caller's; a match is kept only where the room is one of their chats (D3), so channels, the note to self and the
/// changelog are left out. The rooms are listed once per search, the channel rooms only when a match is in a group room.
/// </summary>
internal sealed class TalkChatSearch(TalkChatClient talk, TalkChannelRooms channels, TalkIdentities identities, TalkLinks links) : IChatSearch
{
    public async Task<Result<ChatSearchHits>> SearchAsync(string text, int from, int size, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var page = await talk.SearchMessagesAsync(text, from, size, cancellationToken).ConfigureAwait(false);
        if (!page.IsSuccess)
        {
            return page.Error;
        }

        var more = page.Value.Read >= size;
        if (page.Value.Entries.Count == 0)
        {
            return new ChatSearchHits([], more);
        }

        var listed = await talk.ListRoomsAsync(cancellationToken).ConfigureAwait(false);
        if (!listed.IsSuccess)
        {
            return listed.Error;
        }

        var chats = listed.Value.Where(TalkChatRooms.IsChat).ToDictionary(r => r.Token, StringComparer.Ordinal);
        var found = page.Value.Entries.Where(e => chats.ContainsKey(e.Token)).ToList();
        if (found.Any(e => !chats[e.Token].IsOneToOne))
        {
            var channelTokens = await channels.TokensAsync(cancellationToken).ConfigureAwait(false);
            found = found.Where(e => !channelTokens.Contains(e.Token)).ToList();
        }

        var loaded = await identities.LoadAsync(found.Where(e => e.Actor.IsUser).Select(e => e.Actor.Id), cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error!;
        }

        var hits = found.Select(e => new ChatMessageHit(TalkIds.Decimal(e.MessageId), e.Token, e.Timestamp, Summary(e.Excerpt))
        {
            From = Sender(e, chats[e.Token]),
            WebUrl = links.ConversationUrl(e.Token) is { } url ? url + "#message_" + TalkIds.Decimal(e.MessageId) : null,
        }).ToList();
        return new ChatSearchHits(hits, more);
    }

    /// <summary>Talk's excerpt as Microsoft Search writes one: plain text, a cut marked with three dots.</summary>
    internal static string Summary(string excerpt) => excerpt.Replace("…", "...", StringComparison.Ordinal);

    private ChatSender Sender(TalkSearchEntry entry, TalkRoom room)
    {
        if (entry.Actor.IsUser && identities.UserOf(entry.Actor.Id) is { } user)
        {
            return new ChatSender(user.DisplayName, user.Mail ?? user.UserPrincipalName);
        }

        return new ChatSender(SenderName(entry.Title, room.IsOneToOne ? null : room.DisplayName), null);
    }

    /// <summary>
    /// The sender's name from Talk's entry title: the whole title in a one-to-one chat, else "{sender} in {conversation}"
    /// less the conversation and the (translated) word before it.
    /// </summary>
    internal static string? SenderName(string title, string? conversation)
    {
        var name = title.Trim();
        if (!string.IsNullOrEmpty(conversation) && name.Length > conversation.Length && name.EndsWith(conversation, StringComparison.Ordinal))
        {
            var rest = name[..^conversation.Length].TrimEnd();
            var cut = rest.LastIndexOf(' ');
            name = cut > 0 ? rest[..cut].TrimEnd() : rest;
        }

        return name.Length == 0 ? null : name;
    }
}
