using Nightware.Application.Common.Query;
using Nightware.Domain.Chats;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Chats;

/// <summary>Checks on message bodies and reactions, shared by chat and channel messages.</summary>
public static class ChatMessageRules
{
    /// <summary>The backend's limit on a message (Talk's 32,000 characters).</summary>
    public const int MaxBodyLength = 32000;

    /// <summary>Graph's legacy reaction names and the emoji Teams shows for them.</summary>
    private static readonly Dictionary<string, string> LegacyReactions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["like"] = "👍",
        ["heart"] = "❤️",
        ["laugh"] = "😆",
        ["surprised"] = "😮",
        ["sad"] = "😢",
        ["angry"] = "😡",
    };

    public static Error? Validate(ChatMessageDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(draft.Body.Content))
        {
            return Error.Validation("A message needs a body.", "body");
        }

        if (draft.Body.Content.Length > MaxBodyLength)
        {
            return Error.Validation($"A message body has at most {MaxBodyLength} characters.", "body");
        }

        var ids = new HashSet<int>();
        foreach (var mention in draft.Mentions)
        {
            if (!ids.Add(mention.Id))
            {
                return Error.Validation($"Mention id {mention.Id} is used twice.", "mentions");
            }

            if (!mention.IsConversation && string.IsNullOrWhiteSpace(mention.User))
            {
                return Error.Validation("A mention names a user (mentioned.user.id) or the conversation.", "mentions");
            }
        }

        return null;
    }

    /// <summary>The emoji a reaction stands for: a unicode reaction as given, a legacy name as Teams shows it.</summary>
    public static Result<string> Reaction(string? reactionType)
    {
        var text = reactionType?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 64)
        {
            return Error.Validation("reactionType must be an emoji.", "reactionType");
        }

        return LegacyReactions.TryGetValue(text, out var emoji) ? emoji : text;
    }
}

/// <summary>Graph <c>GET /chats/{id}/messages</c>.</summary>
public sealed class ListChatMessages(IChatMessages messages)
{
    public async Task<Result<Page<ChatMessage>>> ExecuteAsync(string chatId, ChatPageQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var top = ChatRules.PageSize(query.Top);
        return top.IsSuccess ? await messages.ListAsync(chatId, query, cancellationToken).ConfigureAwait(false) : top.Error;
    }
}

/// <summary>Graph <c>GET /chats/{id}/messages/{messageId}</c>.</summary>
public sealed class GetChatMessage(IChatMessages messages)
{
    public Task<Result<ChatMessage>> ExecuteAsync(string chatId, string messageId, CancellationToken cancellationToken) =>
        messages.GetAsync(chatId, messageId, cancellationToken);
}

/// <summary>Graph <c>POST /chats/{id}/messages</c>.</summary>
public sealed class SendChatMessage(IChatMessages messages)
{
    public async Task<Result<ChatMessage>> ExecuteAsync(string chatId, ChatMessageDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return ChatMessageRules.Validate(draft) is { } invalid
            ? invalid
            : await messages.SendAsync(chatId, draft, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Graph <c>PATCH /chats/{id}/messages/{messageId}</c>: the sender changes the body.</summary>
public sealed class UpdateChatMessage(IChatMessages messages)
{
    public async Task<Result> ExecuteAsync(string chatId, string messageId, ChatMessageDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return ChatMessageRules.Validate(draft) is { } invalid
            ? invalid
            : await messages.UpdateAsync(chatId, messageId, draft, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Graph <c>POST …/messages/{messageId}/softDelete</c>.</summary>
public sealed class SoftDeleteChatMessage(IChatMessages messages)
{
    public Task<Result> ExecuteAsync(string chatId, string messageId, CancellationToken cancellationToken) =>
        messages.SoftDeleteAsync(chatId, messageId, cancellationToken);
}

/// <summary>Graph <c>POST …/messages/{messageId}/setReaction</c>.</summary>
public sealed class SetChatMessageReaction(IChatMessages messages)
{
    public async Task<Result> ExecuteAsync(string chatId, string messageId, string? reactionType, CancellationToken cancellationToken)
    {
        var reaction = ChatMessageRules.Reaction(reactionType);
        return reaction.IsSuccess
            ? await messages.SetReactionAsync(chatId, messageId, reaction.Value, cancellationToken).ConfigureAwait(false)
            : reaction.Error;
    }
}

/// <summary>Graph <c>POST …/messages/{messageId}/unsetReaction</c>.</summary>
public sealed class UnsetChatMessageReaction(IChatMessages messages)
{
    public async Task<Result> ExecuteAsync(string chatId, string messageId, string? reactionType, CancellationToken cancellationToken)
    {
        var reaction = ChatMessageRules.Reaction(reactionType);
        return reaction.IsSuccess
            ? await messages.UnsetReactionAsync(chatId, messageId, reaction.Value, cancellationToken).ConfigureAwait(false)
            : reaction.Error;
    }
}

/// <summary>Graph <c>GET /chats/{id}/pinnedMessages</c>.</summary>
public sealed class ListPinnedChatMessages(IChatPins pins)
{
    public Task<Result<IReadOnlyList<PinnedChatMessage>>> ExecuteAsync(string chatId, CancellationToken cancellationToken) =>
        pins.ListAsync(chatId, cancellationToken);
}

/// <summary>Graph <c>POST /chats/{id}/pinnedMessages</c>.</summary>
public sealed class PinChatMessage(IChatPins pins)
{
    public async Task<Result<PinnedChatMessage>> ExecuteAsync(string chatId, string? messageId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return Error.Validation("message@odata.bind must name a message of this chat.", "message@odata.bind");
        }

        return await pins.PinAsync(chatId, messageId, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Graph <c>DELETE /chats/{id}/pinnedMessages/{pinnedMessageId}</c>.</summary>
public sealed class UnpinChatMessage(IChatPins pins)
{
    public Task<Result> ExecuteAsync(string chatId, string pinnedMessageId, CancellationToken cancellationToken) =>
        pins.UnpinAsync(chatId, pinnedMessageId, cancellationToken);
}

/// <summary>Microsoft Search (<c>POST /search/query</c>) for <c>chatMessage</c>: the caller's chat and channel messages by KQL.</summary>
public sealed class SearchChatMessages(IChatSearch search)
{
    /// <summary>
    /// The most hits one request returns: Graph's default for <c>chatMessage</c>, and Nextcloud's own cap on a unified
    /// search page. A larger <c>size</c> is read as this, with <c>moreResultsAvailable</c> saying whether more match.
    /// </summary>
    public const int MaxSize = 25;

    public const int DefaultSize = MaxSize;

    /// <summary>The longest query text taken.</summary>
    public const int MaxTextLength = 256;

    /// <summary>The fields a chat message query may hold: free text and Microsoft Search's Teams scope terms.</summary>
    public static IReadOnlySet<SearchField> Fields { get; } = new HashSet<SearchField>
    {
        SearchField.Any,
        SearchField.From,
        SearchField.To,
        SearchField.Sent,
        SearchField.HasAttachments,
        SearchField.IsRead,
        SearchField.IsMentioned,
        SearchField.Mentions,
    };

    public Task<Result<ChatSearchHits>> ExecuteAsync(SearchQuery? query, int from, int size, CancellationToken cancellationToken)
    {
        if (query is null || (query.Terms.Count == 0 && query.AnyOf.Count == 0))
        {
            return Task.FromResult<Result<ChatSearchHits>>(Error.Validation("query.queryString is required.", "query.queryString"));
        }

        if (query.Terms.Concat(query.AnyOf.SelectMany(g => g)).FirstOrDefault(t => !Fields.Contains(t.Field)) is { } unsupported)
        {
            return Task.FromResult<Result<ChatSearchHits>>(
                Error.Validation($"Chat messages can't be searched by {unsupported.Field}.", "query.queryString"));
        }

        if (from < 0)
        {
            return Task.FromResult<Result<ChatSearchHits>>(Error.Validation("from cannot be negative.", "from"));
        }

        if (size < 1)
        {
            return Task.FromResult<Result<ChatSearchHits>>(Error.Validation("size must be at least 1.", "size"));
        }

        return search.SearchAsync(query, from, Math.Min(size, MaxSize), cancellationToken);
    }
}
