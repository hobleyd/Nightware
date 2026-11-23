using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Nightware.Adapters.Dav.Chats.Talk;
using Nightware.Application.Chats;
using Nightware.Application.Teams;
using Nightware.Domain.Chats;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Teams;

/// <summary>
/// <see cref="IChannelMessages"/> on a channel room's chat (ADR 0009 D4–D5), through <see cref="TalkMessageFeed"/>.
/// A post is a Talk thread's first message (its subject the thread's title) or a top-level message sent outside threads;
/// a reply is a message in the thread. Talk refuses a thread reply to a message that didn't start a thread, so such a
/// post takes no replies.
/// </summary>
internal sealed class TalkChannelMessages(TalkChatClient talk, TalkChannelStore channels, TalkMessageFeed feed) : IChannelMessages
{
    /// <summary>Talk messages read per round of a delta.</summary>
    internal const int DeltaRoundSize = 100;

    /// <summary>Rounds a delta reads before it hands back a link to read on.</summary>
    internal const int DeltaRounds = 5;

    /// <summary>System messages about another message, whose parent a delta re-reports.</summary>
    private static readonly HashSet<string> ChangesToParent = new(StringComparer.Ordinal)
    {
        "message_edited", "message_deleted", "thread_renamed", "reaction", "reaction_revoked", "reaction_deleted",
    };

    public async Task<Result<Page<ChatMessage>>> ListPostsAsync(string teamId, string channelId, ChatPageQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var conversation = await channels.ConversationAsync(teamId, channelId, cancellationToken).ConfigureAwait(false);
        return conversation.IsSuccess
            ? await feed.PageAsync(conversation.Value, query.Top, query.SkipToken, null, TalkMessageCodec.IsPost, cancellationToken).ConfigureAwait(false)
            : conversation.Error;
    }

    public async Task<Result<Page<ChatMessage>>> ListRepliesAsync(ChannelMessageRef post, ChatPageQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(query);
        var found = await PostAsync(post, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            return found.Error;
        }

        var (conversation, message) = found.Value;
        if (!message.IsThread)
        {
            return new Page<ChatMessage>([]);
        }

        return await feed.PageAsync(conversation, query.Top, query.SkipToken, message.Id, m => TalkMessageCodec.IsThreadReply(m) && m.ThreadId == message.Id, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Result<ChatMessage>> GetAsync(ChannelMessageRef message, CancellationToken cancellationToken)
    {
        var found = await ResolveAsync(message, cancellationToken).ConfigureAwait(false);
        return found.IsSuccess ? await feed.DecodeOneAsync(found.Value.Conversation, found.Value.Message, cancellationToken).ConfigureAwait(false) : found.Error;
    }

    public async Task<Result<ChatMessage>> PostAsync(string teamId, string channelId, ChatMessageDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var conversation = await channels.ConversationAsync(teamId, channelId, cancellationToken).ConfigureAwait(false);
        if (!conversation.IsSuccess)
        {
            return conversation.Error;
        }

        var text = await feed.EncodeAsync(draft, cancellationToken).ConfigureAwait(false);
        if (!text.IsSuccess)
        {
            return text.Error;
        }

        // Talk starts a thread only with a title: without a subject the post's first line is its title (ADR 0009 D5).
        var title = string.IsNullOrWhiteSpace(draft.Subject) ? TalkMessageCodec.DefaultThreadTitle(text.Value) : draft.Subject.Trim();
        var sent = await talk.PostAsync(conversation.Value.Token, new TalkPost(text.Value) { ThreadTitle = title }, cancellationToken).ConfigureAwait(false);
        return sent.IsSuccess ? await feed.DecodeOneAsync(conversation.Value, sent.Value, cancellationToken).ConfigureAwait(false) : sent.Error;
    }

    public async Task<Result<ChatMessage>> ReplyAsync(ChannelMessageRef post, ChatMessageDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(draft);
        var found = await PostAsync(post, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            return found.Error;
        }

        var (conversation, message) = found.Value;
        if (!message.IsThread)
        {
            return NoThread();
        }

        var text = await feed.EncodeAsync(draft, cancellationToken).ConfigureAwait(false);
        if (!text.IsSuccess)
        {
            return text.Error;
        }

        var sent = await talk.PostAsync(conversation.Token, new TalkPost(text.Value) { ThreadId = message.Id }, cancellationToken).ConfigureAwait(false);
        return sent.IsSuccess ? await feed.DecodeOneAsync(conversation, sent.Value, cancellationToken).ConfigureAwait(false) : sent.Error;
    }

    public async Task<Result> UpdateAsync(ChannelMessageRef message, ChatMessageDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(draft);
        var found = await ResolveAsync(message, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            return found.Error;
        }

        var (conversation, target) = found.Value;
        if (draft.Subject is not null && !target.IsThread)
        {
            return Error.Validation("This post was sent outside a thread, so it has no subject to change.", "subject");
        }

        var text = await feed.EncodeAsync(draft, cancellationToken).ConfigureAwait(false);
        if (!text.IsSuccess)
        {
            return text.Error!;
        }

        var edited = await talk.EditAsync(conversation.Token, target.Id, text.Value, cancellationToken).ConfigureAwait(false);
        if (!edited.IsSuccess)
        {
            return edited.Error!;
        }

        // A post whose title was only its first line keeps that rule: a new body gives it a new default title.
        var title = draft.Subject?.Trim()
                    ?? (target.IsThread && !TalkMessageCodec.IsThreadReply(target) && target.ThreadTitle == TalkMessageCodec.DefaultThreadTitle(target.Text)
                        ? TalkMessageCodec.DefaultThreadTitle(text.Value)
                        : null);
        return title is not null && title != target.ThreadTitle
            ? await talk.RenameThreadAsync(conversation.Token, target.Id, title, cancellationToken).ConfigureAwait(false)
            : Result.Success();
    }

    public async Task<Result> SoftDeleteAsync(ChannelMessageRef message, CancellationToken cancellationToken)
    {
        var found = await ResolveAsync(message, cancellationToken).ConfigureAwait(false);
        return found.IsSuccess ? await talk.DeleteMessageAsync(found.Value.Conversation.Token, found.Value.Message.Id, cancellationToken).ConfigureAwait(false) : found.Error;
    }

    public async Task<Result> SetReactionAsync(ChannelMessageRef message, string reaction, CancellationToken cancellationToken)
    {
        var found = await ResolveAsync(message, cancellationToken).ConfigureAwait(false);
        return found.IsSuccess ? await talk.ReactAsync(found.Value.Conversation.Token, found.Value.Message.Id, reaction, cancellationToken).ConfigureAwait(false) : found.Error;
    }

    public async Task<Result> UnsetReactionAsync(ChannelMessageRef message, string reaction, CancellationToken cancellationToken)
    {
        var found = await ResolveAsync(message, cancellationToken).ConfigureAwait(false);
        return found.IsSuccess ? await talk.UnreactAsync(found.Value.Conversation.Token, found.Value.Message.Id, reaction, cancellationToken).ConfigureAwait(false) : found.Error;
    }

    /// <summary>
    /// Messages new or changed since the token (ADR 0009 D5). The first sync pages through the posts newest first,
    /// remembering the newest message at its start; later syncs read Talk's chat after the last message seen. New posts
    /// and replies are reported, and so is the message an edit, delete, rename or reaction is about (re-read as it is now).
    /// </summary>
    public async Task<Result<DeltaPage<ChatMessage>>> DeltaAsync(string teamId, string channelId, DeltaToken? token, int pageSize, CancellationToken cancellationToken)
    {
        var state = token is { } given ? ChannelDeltaToken.Decode(given.Value) : null;
        if (token is not null && (state is null || state.Room != channelId))
        {
            return Error.SyncStateGone("The delta token is not valid for this channel; sync again from the start.");
        }

        var conversation = await channels.ConversationAsync(teamId, channelId, cancellationToken).ConfigureAwait(false);
        if (!conversation.IsSuccess)
        {
            return conversation.Error;
        }

        return state is { Cursor: null }
            ? await ChangesAsync(conversation.Value, state.HighWater, cancellationToken).ConfigureAwait(false)
            : await InitialAsync(conversation.Value, state, pageSize, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<DeltaPage<ChatMessage>>> InitialAsync(TalkConversation conversation, ChannelDeltaToken? state, int pageSize, CancellationToken cancellationToken)
    {
        var highWater = state?.HighWater;
        if (highWater is null)
        {
            var newest = await talk.HistoryAsync(conversation.Token, null, 1, null, cancellationToken).ConfigureAwait(false);
            if (!newest.IsSuccess)
            {
                return newest.Error;
            }

            highWater = newest.Value.Messages.Count > 0 ? newest.Value.Messages.Max(m => m.Id) : 0;
        }

        var page = await feed.PageAsync(conversation, pageSize, state?.Cursor, null, TalkMessageCodec.IsPost, cancellationToken).ConfigureAwait(false);
        if (!page.IsSuccess)
        {
            return page.Error.Kind == ErrorKind.Validation ? Error.SyncStateGone("The delta token is not valid.") : page.Error;
        }

        var entries = page.Value.Items.Select(m => (DeltaEntry<ChatMessage>)new DeltaEntry<ChatMessage>.Upsert(m)).ToList();
        return page.Value.SkipToken is { } next
            ? DeltaPage<ChatMessage>.More(entries, new ChannelDeltaToken(conversation.Token, highWater.Value, next).ToToken())
            : DeltaPage<ChatMessage>.Done(entries, new ChannelDeltaToken(conversation.Token, highWater.Value, null).ToToken());
    }

    private async Task<Result<DeltaPage<ChatMessage>>> ChangesAsync(TalkConversation conversation, long after, CancellationToken cancellationToken)
    {
        var read = new List<TalkMessage>();
        var changed = new Dictionary<long, TalkMessage>();
        var cursor = after;
        var more = false;
        for (var round = 0; round < DeltaRounds; round++)
        {
            var page = await talk.FutureAsync(conversation.Token, cursor, DeltaRoundSize, cancellationToken).ConfigureAwait(false);
            if (!page.IsSuccess)
            {
                return page.Error;
            }

            if (page.Value.LastGiven is not { } lastGiven)
            {
                more = false;
                break;
            }

            foreach (var message in page.Value.Messages)
            {
                read.Add(message);
                if (TalkMessageCodec.IsListed(message))
                {
                    changed[message.Id] = message;
                }
                else if (message.IsSystem && ChangesToParent.Contains(message.SystemMessage) && message.Parent is { } parent && TalkMessageCodec.IsListed(parent))
                {
                    changed.TryAdd(parent.Id, parent);
                }
            }

            cursor = Math.Max(cursor, lastGiven);
            more = page.Value.Messages.Count >= DeltaRoundSize;
            if (!more)
            {
                break;
            }
        }

        // A post seen only as an event's parent is re-read: the parent Talk embeds lacks who reacted.
        var current = new List<TalkMessage>(changed.Count);
        foreach (var message in changed.Values.OrderBy(m => m.Id))
        {
            if (read.Any(m => m.Id == message.Id))
            {
                current.Add(message);
                continue;
            }

            var reread = await talk.MessageAsync(conversation.Token, message.Id, cancellationToken).ConfigureAwait(false);
            if (reread.IsSuccess)
            {
                current.Add(reread.Value);
            }
            else if (reread.Error.Kind != ErrorKind.NotFound)
            {
                return reread.Error;
            }
        }

        var decoded = await feed.DecodeAsync(conversation, current.Where(TalkMessageCodec.IsListed).ToList(), TalkMessageCodec.DeletionTimes(read), cancellationToken).ConfigureAwait(false);
        if (!decoded.IsSuccess)
        {
            return decoded.Error;
        }

        var entries = decoded.Value.Select(m => (DeltaEntry<ChatMessage>)new DeltaEntry<ChatMessage>.Upsert(m)).ToList();
        var next = new ChannelDeltaToken(conversation.Token, cursor, null).ToToken();
        return more ? DeltaPage<ChatMessage>.More(entries, next) : DeltaPage<ChatMessage>.Done(entries, next);
    }

    /// <summary>A post of the channel, or NotFound for a reply, Talk's bookkeeping or an id the channel lacks.</summary>
    private async Task<Result<(TalkConversation Conversation, TalkMessage Message)>> PostAsync(ChannelMessageRef post, CancellationToken cancellationToken)
    {
        if (!TalkIds.TryMessageId(post.MessageId, out var postId))
        {
            return NoMessage();
        }

        var conversation = await channels.ConversationAsync(post.TeamId, post.ChannelId, cancellationToken).ConfigureAwait(false);
        if (!conversation.IsSuccess)
        {
            return conversation.Error;
        }

        var message = await talk.MessageAsync(conversation.Value.Token, postId, cancellationToken).ConfigureAwait(false);
        if (!message.IsSuccess)
        {
            return message.Error.Kind == ErrorKind.NotFound ? NoMessage() : message.Error;
        }

        return TalkMessageCodec.IsListed(message.Value) && TalkMessageCodec.IsPost(message.Value)
            ? (conversation.Value, message.Value)
            : NoMessage();
    }

    /// <summary>A post, or a reply to the post the reference names.</summary>
    private async Task<Result<(TalkConversation Conversation, TalkMessage Message)>> ResolveAsync(ChannelMessageRef message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.ReplyId is null)
        {
            return await PostAsync(message, cancellationToken).ConfigureAwait(false);
        }

        if (!TalkIds.TryMessageId(message.MessageId, out var postId) || !TalkIds.TryMessageId(message.ReplyId, out var replyId))
        {
            return NoMessage();
        }

        var conversation = await channels.ConversationAsync(message.TeamId, message.ChannelId, cancellationToken).ConfigureAwait(false);
        if (!conversation.IsSuccess)
        {
            return conversation.Error;
        }

        var reply = await talk.MessageAsync(conversation.Value.Token, replyId, cancellationToken).ConfigureAwait(false);
        if (!reply.IsSuccess)
        {
            return reply.Error.Kind == ErrorKind.NotFound ? NoMessage() : reply.Error;
        }

        return TalkMessageCodec.IsListed(reply.Value) && TalkMessageCodec.IsThreadReply(reply.Value) && reply.Value.ThreadId == postId
            ? (conversation.Value, reply.Value)
            : NoMessage();
    }

    private static Error NoMessage() => Error.NotFound("The message was not found in this channel.");

    private static Error NoThread() =>
        Error.Validation("This post was sent outside a thread (in Talk's own client), so it can't take replies.");
}

/// <summary>
/// A channel delta token: the room, the newest message id when the first sync began (or the last one read since), and,
/// during the first sync, where its listing of posts goes on. Unpadded base64url JSON; the Api signs it into the link.
/// </summary>
internal sealed record ChannelDeltaToken(
    [property: JsonPropertyName("r")] string Room,
    [property: JsonPropertyName("h")] long HighWater,
    [property: JsonPropertyName("c")] string? Cursor)
{
    [JsonPropertyName("v")]
    public int Version { get; init; } = 1;

    public DeltaToken ToToken() => new(Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(this)));

    public static ChannelDeltaToken? Decode(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 512 || !Base64Url.IsValid(token))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ChannelDeltaToken>(Base64Url.DecodeFromChars(token)) is { Version: 1, HighWater: >= 0 } valid
                   && TalkIds.IsToken(valid.Room) && (valid.Cursor is null || TalkIds.TryMessageId(valid.Cursor, out _))
                ? valid
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
