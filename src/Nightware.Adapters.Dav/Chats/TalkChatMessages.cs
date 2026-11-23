using Nightware.Adapters.Dav.Chats.Talk;
using Nightware.Application.Chats;
using Nightware.Domain.Chats;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Chats;

/// <summary>
/// <see cref="IChatMessages"/> on Talk's chat (ADR 0009 D4), as the caller, through <see cref="TalkMessageFeed"/>.
/// The room is read first: it says whether the id is a chat of the caller's and gives <c>@all</c> its name.
/// </summary>
internal sealed class TalkChatMessages(TalkChatClient talk, TalkChatRooms rooms, TalkMessageFeed feed) : IChatMessages
{
    public async Task<Result<Page<ChatMessage>>> ListAsync(string chatId, ChatPageQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var conversation = await ConversationAsync(chatId, cancellationToken).ConfigureAwait(false);
        return conversation.IsSuccess
            ? await feed.PageAsync(conversation.Value, query.Top, query.SkipToken, null, cancellationToken).ConfigureAwait(false)
            : conversation.Error;
    }

    public async Task<Result<ChatMessage>> GetAsync(string chatId, string messageId, CancellationToken cancellationToken)
    {
        var conversation = await ConversationAsync(chatId, cancellationToken).ConfigureAwait(false);
        return conversation.IsSuccess ? await feed.GetAsync(conversation.Value, messageId, cancellationToken).ConfigureAwait(false) : conversation.Error;
    }

    public async Task<Result<ChatMessage>> SendAsync(string chatId, ChatMessageDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var conversation = await ConversationAsync(chatId, cancellationToken).ConfigureAwait(false);
        if (!conversation.IsSuccess)
        {
            return conversation.Error;
        }

        var text = await feed.EncodeAsync(draft, cancellationToken).ConfigureAwait(false);
        if (!text.IsSuccess)
        {
            return text.Error;
        }

        var sent = await talk.PostAsync(conversation.Value.Token, new TalkPost(text.Value), cancellationToken).ConfigureAwait(false);
        return sent.IsSuccess ? await feed.DecodeOneAsync(conversation.Value, sent.Value, cancellationToken).ConfigureAwait(false) : sent.Error;
    }

    public async Task<Result> UpdateAsync(string chatId, string messageId, ChatMessageDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!TalkIds.TryMessageId(messageId, out var id))
        {
            return TalkChatClient.NoMessage();
        }

        var text = await feed.EncodeAsync(draft, cancellationToken).ConfigureAwait(false);
        if (!text.IsSuccess)
        {
            return text.Error!;
        }

        var edited = await talk.EditAsync(chatId, id, text.Value, cancellationToken).ConfigureAwait(false);
        return edited.IsSuccess ? Result.Success() : edited.Error!;
    }

    public Task<Result> SoftDeleteAsync(string chatId, string messageId, CancellationToken cancellationToken) =>
        TalkIds.TryMessageId(messageId, out var id)
            ? talk.DeleteMessageAsync(chatId, id, cancellationToken)
            : Task.FromResult<Result>(TalkChatClient.NoMessage());

    public Task<Result> SetReactionAsync(string chatId, string messageId, string reaction, CancellationToken cancellationToken) =>
        TalkIds.TryMessageId(messageId, out var id)
            ? talk.ReactAsync(chatId, id, reaction, cancellationToken)
            : Task.FromResult<Result>(TalkChatClient.NoMessage());

    public Task<Result> UnsetReactionAsync(string chatId, string messageId, string reaction, CancellationToken cancellationToken) =>
        TalkIds.TryMessageId(messageId, out var id)
            ? talk.UnreactAsync(chatId, id, reaction, cancellationToken)
            : Task.FromResult<Result>(TalkChatClient.NoMessage());

    private async Task<Result<TalkConversation>> ConversationAsync(string chatId, CancellationToken cancellationToken)
    {
        var room = await rooms.FindAsync(chatId, cancellationToken).ConfigureAwait(false);
        return room.IsSuccess ? rooms.Conversation(room.Value) : room.Error;
    }
}

/// <summary><see cref="IChatPins"/> on Talk's pinned messages (pinning is a moderator's action in Talk).</summary>
internal sealed class TalkChatPins(TalkChatClient talk, TalkChatRooms rooms, TalkMessageFeed feed) : IChatPins
{
    public async Task<Result<IReadOnlyList<PinnedChatMessage>>> ListAsync(string chatId, CancellationToken cancellationToken)
    {
        var room = await rooms.FindAsync(chatId, cancellationToken).ConfigureAwait(false);
        if (!room.IsSuccess)
        {
            return room.Error;
        }

        var pinned = await talk.PinnedAsync(chatId, cancellationToken).ConfigureAwait(false);
        if (!pinned.IsSuccess)
        {
            return pinned.Error;
        }

        var messages = await feed.DecodeAsync(rooms.Conversation(room.Value), pinned.Value, new Dictionary<long, DateTimeOffset>(), cancellationToken).ConfigureAwait(false);
        return messages.IsSuccess ? messages.Value.Select(m => new PinnedChatMessage(m.Id, m)).ToList() : messages.Error;
    }

    public async Task<Result<PinnedChatMessage>> PinAsync(string chatId, string messageId, CancellationToken cancellationToken)
    {
        if (!TalkIds.TryMessageId(messageId, out var id))
        {
            return TalkChatClient.NoMessage();
        }

        var room = await rooms.FindAsync(chatId, cancellationToken).ConfigureAwait(false);
        if (!room.IsSuccess)
        {
            return room.Error;
        }

        var pinned = await talk.PinAsync(chatId, id, cancellationToken).ConfigureAwait(false);
        if (!pinned.IsSuccess)
        {
            return pinned.Error!;
        }

        var message = await feed.GetAsync(rooms.Conversation(room.Value), messageId, cancellationToken).ConfigureAwait(false);
        return message.IsSuccess ? new PinnedChatMessage(message.Value.Id, message.Value) : message.Error;
    }

    public Task<Result> UnpinAsync(string chatId, string pinnedMessageId, CancellationToken cancellationToken) =>
        TalkIds.TryMessageId(pinnedMessageId, out var id)
            ? talk.UnpinAsync(chatId, id, cancellationToken)
            : Task.FromResult<Result>(TalkChatClient.NoMessage());
}
