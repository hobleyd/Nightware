using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Nightware.Api.Features.Chats.Dto;
using Nightware.Api.Graph;
using Nightware.Api.Graph.OData;
using Nightware.Api.Graph.Routing;
using Nightware.Application.Chats;
using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Features.Chats;

/// <summary>
/// Chats over Nextcloud Talk conversations (phase 23, ADR 0009 D3–D4). Every chat route is mapped under <c>/chats</c>,
/// <c>/me/chats</c> and <c>/users/{x}/chats</c> (the caller only), as Graph serves them; <c>POST /chats</c> is root only.
/// </summary>
public static class ChatsEndpoints
{
    public static RouteGroupBuilder MapChatsEndpoints(this RouteGroupBuilder graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        graph.MapPost("/chats", CreateAsync);
        MapChats(graph);
        foreach (var scope in graph.MapMailboxGroups())
        {
            MapChats(scope);
        }

        return graph;
    }

    private static void MapChats(IEndpointRouteBuilder group)
    {
        const string Chat = "/chats/{chatId}";
        const string Message = Chat + "/messages/{messageId}";
        group.MapGet("/chats", ListAsync);
        group.MapGet(Chat, GetAsync);
        group.MapPatch(Chat, UpdateAsync);
        group.MapPost(Chat + "/hideForUser", HideAsync);
        group.MapPost(Chat + "/unhideForUser", UnhideAsync);
        group.MapPost(Chat + "/markChatReadForUser", MarkReadAsync);
        group.MapPost(Chat + "/markChatUnreadForUser", MarkUnreadAsync);
        group.MapGet(Chat + "/members", ListMembersAsync);
        group.MapPost(Chat + "/members", AddMemberAsync);
        group.MapGet(Chat + "/members/{membershipId}", GetMemberAsync);
        group.MapDelete(Chat + "/members/{membershipId}", RemoveMemberAsync);
        group.MapGet(Chat + "/messages", ListMessagesAsync);
        group.MapPost(Chat + "/messages", SendMessageAsync);
        group.MapGet(Message, GetMessageAsync);
        group.MapPatch(Message, UpdateMessageAsync);
        group.MapPost(Message + "/softDelete", SoftDeleteMessageAsync);
        group.MapPost(Message + "/setReaction", SetReactionAsync);
        group.MapPost(Message + "/unsetReaction", UnsetReactionAsync);
        group.MapGet(Chat + "/pinnedMessages", ListPinnedAsync);
        group.MapPost(Chat + "/pinnedMessages", PinAsync);
        group.MapDelete(Chat + "/pinnedMessages/{pinnedMessageId}", UnpinAsync);
    }

    private static async Task<IResult> ListAsync(Mailbox mailbox, IUserContext caller, ODataQuery odata, ListChats useCase, CancellationToken cancellationToken)
    {
        if (ChatRequests.OwnChats(mailbox, caller) is { } denied)
        {
            return GraphResults.Error(denied);
        }

        var query = ChatRequests.PageQuery(odata.Query, "members", "lastMessagePreview");
        if (!query.IsSuccess)
        {
            return GraphResults.Error(query.Error);
        }

        var withMembers = odata.Query.ExpandOf("members") is not null;
        return GraphResults.Match(
            await useCase.ExecuteAsync(query.Value, withMembers, cancellationToken).ConfigureAwait(false),
            page => GraphResults.Page(page, v => ChatDto.From(v.Chat, v.Members, query.Value.WithLastMessage), "chats", odata.Query));
    }

    private static async Task<IResult> GetAsync(Mailbox mailbox, string chatId, IUserContext caller, ODataQuery odata, GetChat useCase, CancellationToken cancellationToken)
    {
        if (ChatRequests.OwnChats(mailbox, caller) is { } denied)
        {
            return GraphResults.Error(denied);
        }

        if (odata.Query.Expand.FirstOrDefault(e => !e.NavigationProperty.Equals("members", StringComparison.OrdinalIgnoreCase)) is { } unsupported)
        {
            return GraphResults.Error(Error.Validation($"'{unsupported.NavigationProperty}' can't be expanded here.", "$expand"));
        }

        var withMembers = odata.Query.ExpandOf("members") is not null;
        return GraphResults.Match(
            await useCase.ExecuteAsync(chatId, withMembers, cancellationToken).ConfigureAwait(false),
            view => GraphResults.Entity(ChatDto.From(view.Chat, view.Members), "chats", odata.Query.Select, odata.Query.Expand));
    }

    private static async Task<IResult> CreateAsync(JsonObject? body, CreateChat useCase, CancellationToken cancellationToken)
    {
        var creation = ChatRequests.Creation(body);
        if (!creation.IsSuccess)
        {
            return GraphResults.Error(creation.Error);
        }

        return GraphResults.Match(
            await useCase.ExecuteAsync(creation.Value, cancellationToken).ConfigureAwait(false),
            chat => GraphResults.Created(ChatDto.From(chat), "chats"));
    }

    private static async Task<IResult> UpdateAsync(
        Mailbox mailbox, string chatId, JsonObject? body, IUserContext caller, ODataQuery odata, UpdateChat useCase, CancellationToken cancellationToken)
    {
        if (ChatRequests.OwnChats(mailbox, caller) is { } denied)
        {
            return GraphResults.Error(denied);
        }

        return GraphResults.Match(
            await useCase.ExecuteAsync(chatId, ChatRequests.Text(body, "topic"), cancellationToken).ConfigureAwait(false),
            chat => GraphResults.Entity(ChatDto.From(chat), "chats", odata.Query.Select));
    }

    private static async Task<IResult> HideAsync(Mailbox mailbox, string chatId, JsonObject? body, IUserContext caller, HideChat useCase, CancellationToken cancellationToken) =>
        ChatRequests.OwnChats(mailbox, caller) is { } denied
            ? GraphResults.Error(denied)
            : GraphResults.Match(await useCase.ExecuteAsync(chatId, ChatRequests.UserId(body), cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> UnhideAsync(Mailbox mailbox, string chatId, JsonObject? body, IUserContext caller, UnhideChat useCase, CancellationToken cancellationToken) =>
        ChatRequests.OwnChats(mailbox, caller) is { } denied
            ? GraphResults.Error(denied)
            : GraphResults.Match(await useCase.ExecuteAsync(chatId, ChatRequests.UserId(body), cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> MarkReadAsync(Mailbox mailbox, string chatId, JsonObject? body, IUserContext caller, MarkChatRead useCase, CancellationToken cancellationToken) =>
        ChatRequests.OwnChats(mailbox, caller) is { } denied
            ? GraphResults.Error(denied)
            : GraphResults.Match(await useCase.ExecuteAsync(chatId, ChatRequests.UserId(body), cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> MarkUnreadAsync(Mailbox mailbox, string chatId, JsonObject? body, IUserContext caller, MarkChatUnread useCase, CancellationToken cancellationToken) =>
        ChatRequests.OwnChats(mailbox, caller) is { } denied
            ? GraphResults.Error(denied)
            : GraphResults.Match(await useCase.ExecuteAsync(chatId, ChatRequests.UserId(body), cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> ListMembersAsync(
        Mailbox mailbox, string chatId, IUserContext caller, ODataQuery odata, ListChatMembers useCase, CancellationToken cancellationToken)
    {
        if (ChatRequests.OwnChats(mailbox, caller) is { } denied)
        {
            return GraphResults.Error(denied);
        }

        return GraphResults.Match(
            await useCase.ExecuteAsync(chatId, cancellationToken).ConfigureAwait(false),
            members => GraphResults.Collection(members.Select(ConversationMemberDto.From), Members(chatId), odata.Query));
    }

    private static async Task<IResult> GetMemberAsync(
        Mailbox mailbox, string chatId, string membershipId, IUserContext caller, ODataQuery odata, GetChatMember useCase, CancellationToken cancellationToken)
    {
        if (ChatRequests.OwnChats(mailbox, caller) is { } denied)
        {
            return GraphResults.Error(denied);
        }

        return GraphResults.Match(
            await useCase.ExecuteAsync(chatId, membershipId, cancellationToken).ConfigureAwait(false),
            member => GraphResults.Entity(ConversationMemberDto.From(member), Members(chatId), odata.Query.Select));
    }

    private static async Task<IResult> AddMemberAsync(
        Mailbox mailbox, string chatId, JsonObject? body, IUserContext caller, AddChatMember useCase, CancellationToken cancellationToken)
    {
        if (ChatRequests.OwnChats(mailbox, caller) is { } denied)
        {
            return GraphResults.Error(denied);
        }

        var member = ChatRequests.Member(body);
        if (!member.IsSuccess)
        {
            return GraphResults.Error(member.Error);
        }

        return GraphResults.Match(
            await useCase.ExecuteAsync(chatId, member.Value, cancellationToken).ConfigureAwait(false),
            added => GraphResults.Created(ConversationMemberDto.From(added), Members(chatId)));
    }

    private static async Task<IResult> RemoveMemberAsync(
        Mailbox mailbox, string chatId, string membershipId, IUserContext caller, RemoveChatMember useCase, CancellationToken cancellationToken) =>
        ChatRequests.OwnChats(mailbox, caller) is { } denied
            ? GraphResults.Error(denied)
            : GraphResults.Match(await useCase.ExecuteAsync(chatId, membershipId, cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> ListMessagesAsync(
        Mailbox mailbox, string chatId, IUserContext caller, ODataQuery odata, ListChatMessages useCase, CancellationToken cancellationToken)
    {
        if (ChatRequests.OwnChats(mailbox, caller) is { } denied)
        {
            return GraphResults.Error(denied);
        }

        var query = ChatRequests.PageQuery(odata.Query);
        if (!query.IsSuccess)
        {
            return GraphResults.Error(query.Error);
        }

        return GraphResults.Match(
            await useCase.ExecuteAsync(chatId, query.Value, cancellationToken).ConfigureAwait(false),
            page => GraphResults.Page(page, ChatMessageDto.Of, Messages(chatId), odata.Query));
    }

    private static async Task<IResult> GetMessageAsync(
        Mailbox mailbox, string chatId, string messageId, IUserContext caller, ODataQuery odata, GetChatMessage useCase, CancellationToken cancellationToken)
    {
        if (ChatRequests.OwnChats(mailbox, caller) is { } denied)
        {
            return GraphResults.Error(denied);
        }

        return GraphResults.Match(
            await useCase.ExecuteAsync(chatId, messageId, cancellationToken).ConfigureAwait(false),
            message => GraphResults.Entity(ChatMessageDto.Of(message), Messages(chatId), odata.Query.Select));
    }

    private static async Task<IResult> SendMessageAsync(
        Mailbox mailbox, string chatId, JsonObject? body, IUserContext caller, SendChatMessage useCase, CancellationToken cancellationToken)
    {
        if (ChatRequests.OwnChats(mailbox, caller) is { } denied)
        {
            return GraphResults.Error(denied);
        }

        var draft = ChatRequests.Draft(body);
        if (!draft.IsSuccess)
        {
            return GraphResults.Error(draft.Error);
        }

        return GraphResults.Match(
            await useCase.ExecuteAsync(chatId, draft.Value, cancellationToken).ConfigureAwait(false),
            message => GraphResults.Created(ChatMessageDto.Of(message), Messages(chatId)));
    }

    private static async Task<IResult> UpdateMessageAsync(
        Mailbox mailbox, string chatId, string messageId, JsonObject? body, IUserContext caller, UpdateChatMessage useCase, CancellationToken cancellationToken)
    {
        if (ChatRequests.OwnChats(mailbox, caller) is { } denied)
        {
            return GraphResults.Error(denied);
        }

        var draft = ChatRequests.Draft(body);
        return draft.IsSuccess
            ? GraphResults.Match(await useCase.ExecuteAsync(chatId, messageId, draft.Value, cancellationToken).ConfigureAwait(false))
            : GraphResults.Error(draft.Error);
    }

    private static async Task<IResult> SoftDeleteMessageAsync(
        Mailbox mailbox, string chatId, string messageId, IUserContext caller, SoftDeleteChatMessage useCase, CancellationToken cancellationToken) =>
        ChatRequests.OwnChats(mailbox, caller) is { } denied
            ? GraphResults.Error(denied)
            : GraphResults.Match(await useCase.ExecuteAsync(chatId, messageId, cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> SetReactionAsync(
        Mailbox mailbox, string chatId, string messageId, JsonObject? body, IUserContext caller, SetChatMessageReaction useCase, CancellationToken cancellationToken) =>
        ChatRequests.OwnChats(mailbox, caller) is { } denied
            ? GraphResults.Error(denied)
            : GraphResults.Match(await useCase.ExecuteAsync(chatId, messageId, ChatRequests.Text(body, "reactionType"), cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> UnsetReactionAsync(
        Mailbox mailbox, string chatId, string messageId, JsonObject? body, IUserContext caller, UnsetChatMessageReaction useCase, CancellationToken cancellationToken) =>
        ChatRequests.OwnChats(mailbox, caller) is { } denied
            ? GraphResults.Error(denied)
            : GraphResults.Match(await useCase.ExecuteAsync(chatId, messageId, ChatRequests.Text(body, "reactionType"), cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> ListPinnedAsync(
        Mailbox mailbox, string chatId, IUserContext caller, ODataQuery odata, ListPinnedChatMessages useCase, CancellationToken cancellationToken)
    {
        if (ChatRequests.OwnChats(mailbox, caller) is { } denied)
        {
            return GraphResults.Error(denied);
        }

        if (odata.Query.Expand.FirstOrDefault(e => !e.NavigationProperty.Equals("message", StringComparison.OrdinalIgnoreCase)) is { } unsupported)
        {
            return GraphResults.Error(Error.Validation($"'{unsupported.NavigationProperty}' can't be expanded here.", "$expand"));
        }

        var withMessage = odata.Query.ExpandOf("message") is not null;
        return GraphResults.Match(
            await useCase.ExecuteAsync(chatId, cancellationToken).ConfigureAwait(false),
            pins => GraphResults.Collection(
                pins.Select(p => new PinnedChatMessageInfoDto { Id = p.Id, Message = withMessage ? ChatMessageDto.Of(p.Message) : null }),
                $"chats('{Uri.EscapeDataString(chatId)}')/pinnedMessages", odata.Query));
    }

    private static async Task<IResult> PinAsync(
        Mailbox mailbox, string chatId, JsonObject? body, IUserContext caller, PinChatMessage useCase, CancellationToken cancellationToken)
    {
        if (ChatRequests.OwnChats(mailbox, caller) is { } denied)
        {
            return GraphResults.Error(denied);
        }

        var messageId = ChatRequests.MessageOfBind(body, chatId);
        if (!messageId.IsSuccess)
        {
            return GraphResults.Error(messageId.Error);
        }

        return GraphResults.Match(
            await useCase.ExecuteAsync(chatId, messageId.Value, cancellationToken).ConfigureAwait(false),
            pinned => GraphResults.Created(new PinnedChatMessageInfoDto { Id = pinned.Id }, $"chats('{Uri.EscapeDataString(chatId)}')/pinnedMessages"));
    }

    private static async Task<IResult> UnpinAsync(
        Mailbox mailbox, string chatId, string pinnedMessageId, IUserContext caller, UnpinChatMessage useCase, CancellationToken cancellationToken) =>
        ChatRequests.OwnChats(mailbox, caller) is { } denied
            ? GraphResults.Error(denied)
            : GraphResults.Match(await useCase.ExecuteAsync(chatId, pinnedMessageId, cancellationToken).ConfigureAwait(false));

    private static string Members(string chatId) => $"chats('{Uri.EscapeDataString(chatId)}')/members";

    private static string Messages(string chatId) => $"chats('{Uri.EscapeDataString(chatId)}')/messages";
}
