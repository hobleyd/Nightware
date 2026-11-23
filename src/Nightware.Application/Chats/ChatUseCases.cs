using Nightware.Application.Common.Identity;
using Nightware.Domain.Chats;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Chats;

/// <summary>A chat with its members, when <c>$expand=members</c> asked for them.</summary>
public sealed record ChatView(Chat Chat, IReadOnlyList<ChatMember>? Members = null);

/// <summary>Checks shared by the chat use cases.</summary>
public static class ChatRules
{
    /// <summary>A page size within Graph's limit; null takes the default.</summary>
    public static Result<int> PageSize(int? top) => top switch
    {
        null => ChatLimits.DefaultPageSize,
        < 1 or > ChatLimits.MaxPageSize => Error.Validation($"$top must be between 1 and {ChatLimits.MaxPageSize}.", "$top"),
        { } n => n,
    };

    /// <summary>
    /// Whether <paramref name="user"/> (a directory id, UPN or address) is the caller. The <c>…ForUser</c> actions act on
    /// the caller's own view, because the backend keeps each person's view under their own credential.
    /// </summary>
    public static bool IsCaller(Principal caller, string? user)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return !string.IsNullOrWhiteSpace(user)
               && (string.Equals(user, caller.Id, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(user, caller.Upn, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(user, caller.Mail, StringComparison.OrdinalIgnoreCase));
    }

    public static Error NotTheCaller() => Error.Forbidden("A chat's view can only be changed by its own user: 'user.id' must be the caller.");
}

/// <summary>Graph <c>GET /me/chats</c> (and <c>$expand=members</c>, <c>lastMessagePreview</c>).</summary>
public sealed class ListChats(IChatStore store, IChatMembers members)
{
    public async Task<Result<Page<ChatView>>> ExecuteAsync(ChatPageQuery query, bool withMembers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var top = ChatRules.PageSize(query.Top);
        if (!top.IsSuccess)
        {
            return top.Error;
        }

        var page = await store.ListAsync(query, cancellationToken).ConfigureAwait(false);
        if (!page.IsSuccess)
        {
            return page.Error;
        }

        var views = new List<ChatView>(page.Value.Items.Count);
        foreach (var chat in page.Value.Items)
        {
            var view = await ChatViews.ViewAsync(chat, withMembers, members, cancellationToken).ConfigureAwait(false);
            if (!view.IsSuccess)
            {
                return view.Error;
            }

            views.Add(view.Value);
        }

        return new Page<ChatView>(views, page.Value.SkipToken);
    }
}

/// <summary>Graph <c>GET /chats/{id}</c>.</summary>
public sealed class GetChat(IChatStore store, IChatMembers members)
{
    public async Task<Result<ChatView>> ExecuteAsync(string chatId, bool withMembers, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(chatId);
        var chat = await store.GetAsync(chatId, cancellationToken).ConfigureAwait(false);
        return chat.IsSuccess ? await ChatViews.ViewAsync(chat.Value, withMembers, members, cancellationToken).ConfigureAwait(false) : chat.Error;
    }
}

internal static class ChatViews
{
    public static async Task<Result<ChatView>> ViewAsync(Chat chat, bool withMembers, IChatMembers members, CancellationToken cancellationToken)
    {
        if (!withMembers)
        {
            return new ChatView(chat);
        }

        var listed = await members.ListAsync(chat.Id, cancellationToken).ConfigureAwait(false);
        return listed.IsSuccess ? new ChatView(chat, listed.Value) : listed.Error;
    }
}

/// <summary>
/// Graph <c>POST /chats</c>: a one-to-one chat with one other person, or a group chat. The caller may be among the
/// members (Graph requires it); they are the chat's owner either way. Meeting chats come with an online meeting.
/// </summary>
public sealed class CreateChat(IChatStore store, IUserContext caller)
{
    public async Task<Result<Chat>> ExecuteAsync(ChatCreation creation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(creation);
        if (creation.Members.Any(m => string.IsNullOrWhiteSpace(m.User)))
        {
            return Error.Validation("Each member needs a user (user@odata.bind).", "members");
        }

        var others = creation.Members
            .Where(m => !ChatRules.IsCaller(caller.Principal, m.User))
            .DistinctBy(m => m.User.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToList();
        switch (creation.ChatType)
        {
            case ChatType.Meeting:
                return Error.Validation("A meeting chat is created with its online meeting, not here.", "chatType");
            case ChatType.OneOnOne when others.Count != 1:
                return Error.Validation("A oneOnOne chat has exactly two members: the caller and one other person.", "members");
            case ChatType.OneOnOne when !string.IsNullOrEmpty(creation.Topic):
                return Error.Validation("A oneOnOne chat has no topic.", "topic");
            case ChatType.Group when others.Count == 0:
                return Error.Validation("A group chat needs at least one member besides the caller.", "members");
        }

        return await store.CreateAsync(creation with { Members = others, Topic = creation.Topic?.Trim() }, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Graph <c>PATCH /chats/{id}</c>: the topic of a group chat.</summary>
public sealed class UpdateChat(IChatStore store)
{
    public async Task<Result<Chat>> ExecuteAsync(string chatId, string? topic, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(chatId);
        if (string.IsNullOrWhiteSpace(topic))
        {
            return Error.Validation("A chat's topic can't be empty.", "topic");
        }

        return await store.RenameAsync(chatId, topic.Trim(), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Graph <c>POST /chats/{id}/hideForUser</c>.</summary>
public sealed class HideChat(IChatStore store, IUserContext caller)
{
    public Task<Result> ExecuteAsync(string chatId, string? user, CancellationToken cancellationToken) =>
        ChatRules.IsCaller(caller.Principal, user)
            ? store.SetHiddenAsync(chatId, true, cancellationToken)
            : Task.FromResult<Result>(ChatRules.NotTheCaller());
}

/// <summary>Graph <c>POST /chats/{id}/unhideForUser</c>.</summary>
public sealed class UnhideChat(IChatStore store, IUserContext caller)
{
    public Task<Result> ExecuteAsync(string chatId, string? user, CancellationToken cancellationToken) =>
        ChatRules.IsCaller(caller.Principal, user)
            ? store.SetHiddenAsync(chatId, false, cancellationToken)
            : Task.FromResult<Result>(ChatRules.NotTheCaller());
}

/// <summary>Graph <c>POST /chats/{id}/markChatReadForUser</c>.</summary>
public sealed class MarkChatRead(IChatStore store, IUserContext caller)
{
    public Task<Result> ExecuteAsync(string chatId, string? user, CancellationToken cancellationToken) =>
        ChatRules.IsCaller(caller.Principal, user)
            ? store.MarkReadAsync(chatId, cancellationToken)
            : Task.FromResult<Result>(ChatRules.NotTheCaller());
}

/// <summary>
/// Graph <c>POST /chats/{id}/markChatUnreadForUser</c>. The backend marks the newest message unread;
/// <c>lastMessageReadDateTime</c> can't be honoured and is ignored.
/// </summary>
public sealed class MarkChatUnread(IChatStore store, IUserContext caller)
{
    public Task<Result> ExecuteAsync(string chatId, string? user, CancellationToken cancellationToken) =>
        ChatRules.IsCaller(caller.Principal, user)
            ? store.MarkUnreadAsync(chatId, cancellationToken)
            : Task.FromResult<Result>(ChatRules.NotTheCaller());
}

/// <summary>Graph <c>GET /chats/{id}/members</c>.</summary>
public sealed class ListChatMembers(IChatMembers members)
{
    public Task<Result<IReadOnlyList<ChatMember>>> ExecuteAsync(string chatId, CancellationToken cancellationToken) =>
        members.ListAsync(chatId, cancellationToken);
}

/// <summary>Graph <c>GET /chats/{id}/members/{membershipId}</c>.</summary>
public sealed class GetChatMember(IChatMembers members)
{
    public Task<Result<ChatMember>> ExecuteAsync(string chatId, string membershipId, CancellationToken cancellationToken) =>
        members.GetAsync(chatId, membershipId, cancellationToken);
}

/// <summary>Graph <c>POST /chats/{id}/members</c>. History is always shared, so <c>visibleHistoryStartDateTime</c> is ignored.</summary>
public sealed class AddChatMember(IChatMembers members)
{
    public async Task<Result<ChatMember>> ExecuteAsync(string chatId, ChatMemberDraft member, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (string.IsNullOrWhiteSpace(member.User))
        {
            return Error.Validation("A member needs a user (user@odata.bind).", "user@odata.bind");
        }

        return await members.AddAsync(chatId, member with { User = member.User.Trim() }, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Graph <c>DELETE /chats/{id}/members/{membershipId}</c> (the caller's own membership leaves the chat).</summary>
public sealed class RemoveChatMember(IChatMembers members)
{
    public Task<Result> ExecuteAsync(string chatId, string membershipId, CancellationToken cancellationToken) =>
        members.RemoveAsync(chatId, membershipId, cancellationToken);
}
