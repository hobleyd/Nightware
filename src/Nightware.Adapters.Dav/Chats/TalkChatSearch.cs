using System.Collections.Concurrent;

using Nightware.Adapters.Dav.Chats.Talk;
using Nightware.Application.Chats;
using Nightware.Application.Common.Query;
using Nightware.Domain.Chats;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Chats;

/// <summary>
/// <see cref="IChatSearch"/> on Talk's message search (ADR 0009 D10), as the caller. Talk applies what it can of the query
/// (<see cref="ChatSearchPlan"/>: one substring, a sent-time range, a sender the directory knows by id or address); every
/// match is then checked against the whole query (<see cref="ChatSearchMatcher"/>), reading the message itself only when
/// the query asks about more text, attachments or mentions. Matches are kept in the caller's chats and channels (D3,
/// D5): the note to self and the changelog are left out. The rooms are listed once per search, the teams' channel rooms
/// only when a match is in a group room.
/// </summary>
internal sealed class TalkChatSearch(TalkChatClient talk, TalkChannelRooms channels, TalkIdentities identities, TalkLinks links) : IChatSearch
{
    /// <summary>How many messages are read at once when the query needs them whole.</summary>
    internal const int MessageReads = 4;

    public async Task<Result<ChatSearchHits>> SearchAsync(SearchQuery query, int from, int size, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var plan = ChatSearchPlan.Of(query);
        var filter = new TalkSearchFilter(plan.Text, plan.Since, plan.Until, await PersonAsync(plan.Sender, cancellationToken).ConfigureAwait(false));
        var page = await talk.SearchMessagesAsync(filter, from, size, cancellationToken).ConfigureAwait(false);
        if (!page.IsSuccess && filter.Person is not null && page.Error.Kind == ErrorKind.Validation)
        {
            // Talk refuses a person Nextcloud has no account for (a directory user who never signed in); the sender is
            // then matched on each hit instead.
            page = await talk.SearchMessagesAsync(filter with { Person = null }, from, size, cancellationToken).ConfigureAwait(false);
        }

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

        var rooms = listed.Value.Where(TalkChatRooms.IsChat).ToDictionary(r => r.Token, StringComparer.Ordinal);
        var found = page.Value.Entries.Where(e => rooms.ContainsKey(e.Token)).ToList();
        var teams = found.Any(e => !rooms[e.Token].IsOneToOne)
            ? await channels.TeamsAsync(cancellationToken).ConfigureAwait(false)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        IReadOnlyDictionary<long, TalkMessage>? messages = null;
        if (ChatSearchMatcher.NeedsMessage(query, plan.Text))
        {
            var read = await MessagesAsync(found, cancellationToken).ConfigureAwait(false);
            if (!read.IsSuccess)
            {
                return read.Error;
            }

            messages = read.Value;
            found = found.Where(e => messages.ContainsKey(e.MessageId)).ToList();
        }

        var people = found.Where(e => e.Actor.IsUser).Select(e => e.Actor.Id)
            .Concat(found.Select(e => rooms[e.Token]).Where(r => r.IsOneToOne).SelectMany(r => new[] { r.Name, r.Self?.Id ?? string.Empty }))
            .Concat(messages?.Values.SelectMany(Mentions).Where(p => p.Type == "user").Select(p => p.Id ?? string.Empty) ?? []);
        var loaded = await identities.LoadAsync(people, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error!;
        }

        var hits = new List<ChatMessageHit>();
        foreach (var entry in found)
        {
            var room = rooms[entry.Token];
            var message = messages?.GetValueOrDefault(entry.MessageId);
            if (!ChatSearchMatcher.Matches(query, Candidate(entry, room, message)))
            {
                continue;
            }

            var id = TalkIds.Decimal(entry.MessageId);
            hits.Add(new ChatMessageHit(id, entry.Timestamp, Summary(entry.Excerpt))
            {
                ChatId = teams.ContainsKey(entry.Token) ? null : entry.Token,
                Channel = teams.TryGetValue(entry.Token, out var team) ? new ChannelIdentity(team, entry.Token) : null,
                From = Sender(entry, room),
                WebUrl = links.ConversationUrl(entry.Token) is { } url ? url + "#message_" + id : null,
            });
        }

        return new ChatSearchHits(hits, more);
    }

    /// <summary>Talk's excerpt as Microsoft Search writes one: plain text, a cut marked with three dots.</summary>
    internal static string Summary(string excerpt) => excerpt.Replace("…", "...", StringComparison.Ordinal);

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

    /// <summary>The message as plain text, as Talk's search shows it: placeholders by name, a mentioned user as <c>@name</c>.</summary>
    internal static string PlainText(TalkMessage message)
    {
        var text = message.Text;
        foreach (var (key, parameter) in message.Parameters)
        {
            var name = parameter.Name ?? parameter.Id ?? string.Empty;
            text = text.Replace("{" + key + "}", parameter.Type == "user" ? "@" + name : name, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>
    /// The message's mentions: its <c>mention-…</c> placeholders (a user, or <c>call</c> for the whole conversation).
    /// Talk also names the sender as an <c>actor</c> parameter, which isn't one.
    /// </summary>
    internal static IEnumerable<TalkParameter> Mentions(TalkMessage message) =>
        message.Parameters.Where(p => p.Key.StartsWith("mention-", StringComparison.Ordinal)).Select(p => p.Value);

    /// <summary>The uid a <c>from:</c> value names when the directory knows it by id, UPN or address; null otherwise (a name).</summary>
    private async Task<string?> PersonAsync(string? sender, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sender))
        {
            return null;
        }

        var account = await identities.AccountNameOfAsync(sender, cancellationToken).ConfigureAwait(false);
        return account.IsSuccess ? account.Value : null;
    }

    /// <summary>Each found message, whole; one gone since the search is left out.</summary>
    private async Task<Result<IReadOnlyDictionary<long, TalkMessage>>> MessagesAsync(IReadOnlyList<TalkSearchEntry> found, CancellationToken cancellationToken)
    {
        var messages = new ConcurrentDictionary<long, TalkMessage>();
        Error? failure = null;
        await Parallel.ForEachAsync(found, new ParallelOptions { MaxDegreeOfParallelism = MessageReads, CancellationToken = cancellationToken }, async (entry, ct) =>
        {
            var message = await talk.MessageAsync(entry.Token, entry.MessageId, ct).ConfigureAwait(false);
            if (message.IsSuccess)
            {
                if (!message.Value.IsDeleted)
                {
                    messages[entry.MessageId] = message.Value;
                }
            }
            else if (message.Error.Kind != ErrorKind.NotFound)
            {
                failure ??= message.Error;
            }
        }).ConfigureAwait(false);
        return failure is { } error ? error : messages;
    }

    private ChatSearchCandidate Candidate(TalkSearchEntry entry, TalkRoom room, TalkMessage? message)
    {
        var self = room.Self is { IsUser: true } me ? me.Id : null;
        var fromSelf = self is not null && entry.Actor.IsUser && entry.Actor.Id == self;
        var candidate = new ChatSearchCandidate(entry.Timestamp)
        {
            Sender = entry.Actor.IsUser ? Names(entry.Actor.Id) : [.. new[] { SenderName(entry.Title, room.IsOneToOne ? null : room.DisplayName) }.OfType<string>()],
            Recipients = !room.IsOneToOne ? [] : fromSelf ? Names(room.Name) : self is null ? [] : Names(self),
            IsRead = fromSelf || entry.MessageId <= room.LastReadMessage,
        };
        if (message is null)
        {
            return candidate;
        }

        var parameters = message.Parameters.Values.ToList();
        var mentions = Mentions(message).ToList();
        return candidate with
        {
            Text = PlainText(message),
            HasAttachment = parameters.Any(p => p.Type == "file"),
            IsMentioned = mentions.Any(p => p.Type == "call" || (p.Type == "user" && p.Id is { } id && id == self)),
            Mentioned = mentions.Where(p => p.Type == "user" && p.Id is not null)
                .Select(p => Names(p.Id!).Append(p.Name ?? string.Empty).Where(n => n.Length > 0).ToList())
                .ToList(),
        };
    }

    /// <summary>A user's names for matching: uid, display name, addresses, directory id with and without dashes.</summary>
    private List<string> Names(string accountName)
    {
        var names = new List<string> { accountName };
        if (identities.UserOf(accountName) is DirectoryUser user)
        {
            names.AddRange(new[] { user.DisplayName, user.Mail, user.UserPrincipalName, user.Id, user.Id?.Replace("-", string.Empty, StringComparison.Ordinal) }
                .OfType<string>().Where(n => n.Length > 0));
        }

        return names;
    }

    private ChatSender Sender(TalkSearchEntry entry, TalkRoom room)
    {
        if (entry.Actor.IsUser && identities.UserOf(entry.Actor.Id) is { } user)
        {
            return new ChatSender(user.DisplayName, user.Mail ?? user.UserPrincipalName);
        }

        return new ChatSender(SenderName(entry.Title, room.IsOneToOne ? null : room.DisplayName), null);
    }
}
