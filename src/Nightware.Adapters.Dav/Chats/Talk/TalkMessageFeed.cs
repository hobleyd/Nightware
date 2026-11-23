using System.Globalization;

using Nightware.Domain.Chats;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Chats.Talk;

/// <summary>
/// Graph messages from a Talk conversation (ADR 0009 D4), for chats and channels alike: pages newest first with Talk's
/// bookkeeping left out, single messages, and drafts encoded as Talk Markdown. It reads who reacted for each reacted
/// message, and resolves every person a batch names in one pass before decoding.
/// </summary>
internal sealed class TalkMessageFeed(TalkChatClient talk, TalkIdentities identities)
{
    /// <summary>Talk messages read per round while filling a Graph page.</summary>
    internal const int RoundSize = 100;

    /// <summary>
    /// Rounds read before a page is returned short (with a link to read on): a long stretch of bookkeeping (a burst of
    /// reactions) must not turn one Graph request into dozens of Talk ones.
    /// </summary>
    internal const int MaxRounds = 5;

    /// <summary>
    /// A page of at most <paramref name="top"/> listed messages, newest first. <paramref name="skipToken"/> is the id to
    /// read on from: the last message the previous page gave, or Talk's <c>X-Chat-Last-Given</c> when a page came back
    /// short, so a page can end anywhere in Talk's history.
    /// </summary>
    public async Task<Result<Page<ChatMessage>>> PageAsync(TalkConversation conversation, int top, string? skipToken, long? threadId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        long? cursor = null;
        if (skipToken is not null)
        {
            if (!TalkIds.TryMessageId(skipToken, out var known))
            {
                return Error.Validation("The $skiptoken is not valid.", "$skiptoken");
            }

            cursor = known;
        }

        var read = new List<TalkMessage>();
        var listed = new List<TalkMessage>();
        string? next = null;
        for (var round = 0; ; round++)
        {
            var page = await talk.HistoryAsync(conversation.Token, cursor, RoundSize, threadId, cancellationToken).ConfigureAwait(false);
            if (!page.IsSuccess)
            {
                return page.Error;
            }

            read.AddRange(page.Value.Messages);
            foreach (var message in page.Value.Messages.OrderByDescending(m => m.Id))
            {
                if (listed.Count == top)
                {
                    // More of this round is left: the next page starts below the last message given.
                    next = TalkIds.Decimal(listed[^1].Id);
                    break;
                }

                if (TalkMessageCodec.IsListed(message))
                {
                    listed.Add(message);
                }
            }

            if (next is not null || page.Value.LastGiven is null)
            {
                break;
            }

            cursor = page.Value.LastGiven;
            if (listed.Count == top || round + 1 == MaxRounds)
            {
                next = TalkIds.Decimal(cursor.Value);
                break;
            }
        }

        var decoded = await DecodeAsync(conversation, listed, TalkMessageCodec.DeletionTimes(read), cancellationToken).ConfigureAwait(false);
        return decoded.IsSuccess ? new Page<ChatMessage>(decoded.Value, next) : decoded.Error;
    }

    /// <summary>One listed message by its Graph id; a malformed id, a missing one or Talk's bookkeeping is NotFound.</summary>
    public async Task<Result<ChatMessage>> GetAsync(TalkConversation conversation, string messageId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (!TalkIds.TryMessageId(messageId, out var id))
        {
            return TalkChatClient.NoMessage();
        }

        var message = await talk.MessageAsync(conversation.Token, id, cancellationToken).ConfigureAwait(false);
        return message.IsSuccess ? await DecodeOneAsync(conversation, message.Value, cancellationToken).ConfigureAwait(false) : message.Error;
    }

    /// <summary>A message Talk just returned (sent or edited), as Graph's.</summary>
    public async Task<Result<ChatMessage>> DecodeOneAsync(TalkConversation conversation, TalkMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!TalkMessageCodec.IsListed(message))
        {
            return TalkChatClient.NoMessage();
        }

        var decoded = await DecodeAsync(conversation, [message], new Dictionary<long, DateTimeOffset>(), cancellationToken).ConfigureAwait(false);
        return decoded.IsSuccess ? decoded.Value[0] : decoded.Error;
    }

    /// <summary>
    /// Listed Talk messages as Graph's: reads who reacted where anyone did, resolves the people named, decodes.
    /// </summary>
    public async Task<Result<IReadOnlyList<ChatMessage>>> DecodeAsync(
        TalkConversation conversation, IReadOnlyList<TalkMessage> messages, IReadOnlyDictionary<long, DateTimeOffset> deletionTimes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(deletionTimes);
        var withReactions = new List<TalkMessage>(messages.Count);
        foreach (var message in messages)
        {
            if (message.ReactionCounts.Count == 0 || message.IsSystem || message.IsDeleted)
            {
                withReactions.Add(message);
                continue;
            }

            var reactions = await talk.ReactionsAsync(conversation.Token, message.Id, cancellationToken).ConfigureAwait(false);
            if (!reactions.IsSuccess)
            {
                return reactions.Error;
            }

            withReactions.Add(message with { Reactions = reactions.Value });
        }

        var loaded = await identities.LoadAsync(TalkMessageCodec.AccountsIn(withReactions), cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error!;
        }

        return withReactions
            .Select(m => TalkMessageCodec.ToChatMessage(m, conversation, identities.Identify, deletionTimes.TryGetValue(m.Id, out var at) ? at : null))
            .OfType<ChatMessage>()
            .ToList();
    }

    /// <summary>
    /// A draft's body as Talk Markdown: each mention's user becomes their uid (<c>@"uid"</c>), a conversation mention
    /// <c>@all</c>. A mention of someone the directory doesn't know is <see cref="ErrorKind.NotFound"/>.
    /// </summary>
    public async Task<Result<string>> EncodeAsync(ChatMessageDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var mentions = new Dictionary<int, string>();
        foreach (var mention in draft.Mentions)
        {
            if (mention.IsConversation)
            {
                mentions[mention.Id] = TalkMessageCodec.MentionAll;
                continue;
            }

            var accountName = await identities.AccountNameOfAsync(mention.User!, cancellationToken).ConfigureAwait(false);
            if (!accountName.IsSuccess)
            {
                return accountName.Error.Kind == ErrorKind.NotFound
                    ? Error.Validation($"Mention {mention.Id.ToString(CultureInfo.InvariantCulture)} names nobody in the directory.", "mentions")
                    : accountName.Error;
            }

            mentions[mention.Id] = TalkMessageCodec.MentionOf(accountName.Value);
        }

        var text = TalkMessageCodec.ToTalkText(draft.Body, mentions);
        return string.IsNullOrWhiteSpace(text) ? Error.Validation("The message body has no text Talk can show.", "body") : text;
    }
}
