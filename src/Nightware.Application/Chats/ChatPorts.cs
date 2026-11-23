using Nightware.Domain.Chats;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Chats;

/// <summary>Limits Graph sets on chat listings.</summary>
public static class ChatLimits
{
    /// <summary>Graph's <c>$top</c> limit for chats and chat messages (ADR 0009 D4).</summary>
    public const int MaxPageSize = 50;

    public const int DefaultPageSize = 50;
}

/// <summary>One page of a listing: at most <paramref name="Top"/> items after the store's own <paramref name="SkipToken"/>.</summary>
public sealed record ChatPageQuery(int Top, string? SkipToken)
{
    /// <summary>Also read each chat's newest message (<c>$expand=lastMessagePreview</c>).</summary>
    public bool WithLastMessage { get; init; }
}

/// <summary>
/// The caller's chats over the chat backend (ADR 0009 D3), as the caller. A chat id the backend doesn't know, or one
/// that isn't the caller's, is <see cref="ErrorKind.NotFound"/>.
/// </summary>
public interface IChatStore
{
    /// <summary>The caller's chats, most recently active first. Channels and the backend's own notes are left out.</summary>
    Task<Result<Page<Chat>>> ListAsync(ChatPageQuery query, CancellationToken cancellationToken);

    Task<Result<Chat>> GetAsync(string chatId, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a chat with <see cref="ChatCreation.Members"/> (the caller is added as its owner). A one-to-one chat that
    /// already exists is returned as it is.
    /// </summary>
    Task<Result<Chat>> CreateAsync(ChatCreation creation, CancellationToken cancellationToken);

    /// <summary>Renames a group chat; a one-to-one chat has no topic (<see cref="ErrorKind.Validation"/>).</summary>
    Task<Result<Chat>> RenameAsync(string chatId, string topic, CancellationToken cancellationToken);

    /// <summary>Hides (archives) or shows the chat for the caller.</summary>
    Task<Result> SetHiddenAsync(string chatId, bool hidden, CancellationToken cancellationToken);

    /// <summary>Moves the caller's read marker to the newest message.</summary>
    Task<Result> MarkReadAsync(string chatId, CancellationToken cancellationToken);

    Task<Result> MarkUnreadAsync(string chatId, CancellationToken cancellationToken);
}

/// <summary>A chat's members. Membership ids are the store's own; an unknown one is <see cref="ErrorKind.NotFound"/>.</summary>
public interface IChatMembers
{
    Task<Result<IReadOnlyList<ChatMember>>> ListAsync(string chatId, CancellationToken cancellationToken);

    Task<Result<ChatMember>> GetAsync(string chatId, string membershipId, CancellationToken cancellationToken);

    /// <summary>Adds a directory user; a user the directory doesn't know is <see cref="ErrorKind.NotFound"/>.</summary>
    Task<Result<ChatMember>> AddAsync(string chatId, ChatMemberDraft member, CancellationToken cancellationToken);

    /// <summary>Removes a member; removing the caller's own membership leaves the chat.</summary>
    Task<Result> RemoveAsync(string chatId, string membershipId, CancellationToken cancellationToken);
}

/// <summary>
/// A chat's messages (ADR 0009 D4). Reading never moves the caller's read marker. A message id the chat doesn't have,
/// or one of the backend's own bookkeeping messages, is <see cref="ErrorKind.NotFound"/>.
/// </summary>
public interface IChatMessages
{
    /// <summary>Newest first.</summary>
    Task<Result<Page<ChatMessage>>> ListAsync(string chatId, ChatPageQuery query, CancellationToken cancellationToken);

    Task<Result<ChatMessage>> GetAsync(string chatId, string messageId, CancellationToken cancellationToken);

    Task<Result<ChatMessage>> SendAsync(string chatId, ChatMessageDraft draft, CancellationToken cancellationToken);

    /// <summary>Replaces the body of one of the caller's messages.</summary>
    Task<Result> UpdateAsync(string chatId, string messageId, ChatMessageDraft draft, CancellationToken cancellationToken);

    /// <summary>Deletes one of the caller's messages; it stays in the chat with an empty body and a deletion time.</summary>
    Task<Result> SoftDeleteAsync(string chatId, string messageId, CancellationToken cancellationToken);

    /// <param name="reaction">An emoji.</param>
    Task<Result> SetReactionAsync(string chatId, string messageId, string reaction, CancellationToken cancellationToken);

    Task<Result> UnsetReactionAsync(string chatId, string messageId, string reaction, CancellationToken cancellationToken);
}

/// <summary>A chat's pinned messages. A pin's id is its message's.</summary>
public interface IChatPins
{
    Task<Result<IReadOnlyList<PinnedChatMessage>>> ListAsync(string chatId, CancellationToken cancellationToken);

    Task<Result<PinnedChatMessage>> PinAsync(string chatId, string messageId, CancellationToken cancellationToken);

    Task<Result> UnpinAsync(string chatId, string pinnedMessageId, CancellationToken cancellationToken);
}
