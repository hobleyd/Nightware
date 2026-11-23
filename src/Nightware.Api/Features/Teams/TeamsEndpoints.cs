using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

using Nightware.Api.Discovery;
using Nightware.Api.Features.Chats;
using Nightware.Api.Features.Chats.Dto;
using Nightware.Api.Features.Files.Drives.Dto;
using Nightware.Api.Features.Teams.Dto;
using Nightware.Api.Graph;
using Nightware.Api.Graph.Json;
using Nightware.Api.Graph.OData;
using Nightware.Api.Graph.Routing;
using Nightware.Application.Common.Identity;
using Nightware.Application.Teams;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Features.Teams;

/// <summary>
/// Teams and channels over Nextcloud Teams (circles) and Talk (phase 24, ADR 0009 D5): the caller's teams, their
/// members, standard channels with posts and replies, a channel's files folder, and deleting a team through
/// <c>DELETE /groups/{id}</c>. A team creation's operation is also served at Graph's own <c>Location</c> form,
/// <c>/teams('{id}')/operations('{opId}')</c>.
/// </summary>
public static class TeamsEndpoints
{
    private const string Team = "/teams/{teamId}";
    private const string Channel = Team + "/channels/{channelId}";
    private const string Message = Channel + "/messages/{messageId}";
    private const string Reply = Message + "/replies/{replyId}";

    public static RouteGroupBuilder MapTeamsEndpoints(this RouteGroupBuilder graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        foreach (var scope in graph.MapMailboxGroups())
        {
            scope.MapGet("/joinedTeams", ListJoinedAsync);
        }

        graph.MapPost("/teams", CreateAsync);
        graph.MapGet(Team, GetAsync);
        graph.MapPatch(Team, UpdateAsync);
        graph.MapDelete("/groups/{groupId}", DeleteAsync);
        graph.MapGet(Team + "/operations/{operationId}", GetOperationAsync);
        graph.MapGet("/teams('{teamId}')/operations('{operationId}')", GetOperationAsync);

        graph.MapGet(Team + "/members", ListMembersAsync);
        graph.MapPost(Team + "/members", AddMemberAsync);
        graph.MapGet(Team + "/members/{membershipId}", GetMemberAsync);
        graph.MapPatch(Team + "/members/{membershipId}", UpdateMemberAsync);
        graph.MapDelete(Team + "/members/{membershipId}", RemoveMemberAsync);

        graph.MapGet(Team + "/channels", ListChannelsAsync);
        graph.MapPost(Team + "/channels", CreateChannelAsync);
        graph.MapGet(Team + "/primaryChannel", GetPrimaryChannelAsync);
        graph.MapGet(Channel, GetChannelAsync);
        graph.MapPatch(Channel, UpdateChannelAsync);
        graph.MapDelete(Channel, DeleteChannelAsync);
        graph.MapGet(Channel + "/filesFolder", GetFilesFolderAsync);
        graph.MapGet(Channel + "/members", ListChannelMembersAsync);

        graph.MapGet(Channel + "/messages", ListMessagesAsync);
        graph.MapPost(Channel + "/messages", SendMessageAsync);
        graph.MapGet(Channel + "/messages/delta", DeltaAsync);
        graph.MapGet(Message, (string teamId, string channelId, string messageId, ODataQuery odata, GetChannelMessage useCase, CancellationToken ct) =>
            GetMessageAsync(new ChannelMessageRef(teamId, channelId, messageId), odata, useCase, ct));
        graph.MapGet(Reply, (string teamId, string channelId, string messageId, string replyId, ODataQuery odata, GetChannelMessage useCase, CancellationToken ct) =>
            GetMessageAsync(new ChannelMessageRef(teamId, channelId, messageId, replyId), odata, useCase, ct));
        graph.MapPatch(Message, (string teamId, string channelId, string messageId, JsonObject? body, UpdateChannelMessage useCase, CancellationToken ct) =>
            UpdateMessageAsync(new ChannelMessageRef(teamId, channelId, messageId), body, useCase, ct));
        graph.MapPatch(Reply, (string teamId, string channelId, string messageId, string replyId, JsonObject? body, UpdateChannelMessage useCase, CancellationToken ct) =>
            UpdateMessageAsync(new ChannelMessageRef(teamId, channelId, messageId, replyId), body, useCase, ct));
        graph.MapPost(Message + "/softDelete", (string teamId, string channelId, string messageId, SoftDeleteChannelMessage useCase, CancellationToken ct) =>
            SoftDeleteAsync(new ChannelMessageRef(teamId, channelId, messageId), useCase, ct));
        graph.MapPost(Reply + "/softDelete", (string teamId, string channelId, string messageId, string replyId, SoftDeleteChannelMessage useCase, CancellationToken ct) =>
            SoftDeleteAsync(new ChannelMessageRef(teamId, channelId, messageId, replyId), useCase, ct));
        graph.MapPost(Message + "/setReaction", (string teamId, string channelId, string messageId, JsonObject? body, SetChannelMessageReaction useCase, CancellationToken ct) =>
            SetReactionAsync(new ChannelMessageRef(teamId, channelId, messageId), body, useCase, ct));
        graph.MapPost(Reply + "/setReaction", (string teamId, string channelId, string messageId, string replyId, JsonObject? body, SetChannelMessageReaction useCase, CancellationToken ct) =>
            SetReactionAsync(new ChannelMessageRef(teamId, channelId, messageId, replyId), body, useCase, ct));
        graph.MapPost(Message + "/unsetReaction", (string teamId, string channelId, string messageId, JsonObject? body, UnsetChannelMessageReaction useCase, CancellationToken ct) =>
            UnsetReactionAsync(new ChannelMessageRef(teamId, channelId, messageId), body, useCase, ct));
        graph.MapPost(Reply + "/unsetReaction", (string teamId, string channelId, string messageId, string replyId, JsonObject? body, UnsetChannelMessageReaction useCase, CancellationToken ct) =>
            UnsetReactionAsync(new ChannelMessageRef(teamId, channelId, messageId, replyId), body, useCase, ct));
        graph.MapGet(Message + "/replies", ListRepliesAsync);
        graph.MapPost(Message + "/replies", SendReplyAsync);
        return graph;
    }

    private static async Task<IResult> ListJoinedAsync(Mailbox mailbox, IUserContext caller, ODataQuery odata, ListJoinedTeams useCase, CancellationToken cancellationToken)
    {
        if ((TeamRequests.OwnTeams(mailbox, caller) ?? TeamRequests.WholeList(odata.Query)) is { } denied)
        {
            return GraphResults.Error(denied);
        }

        return GraphResults.Match(
            await useCase.ExecuteAsync(cancellationToken).ConfigureAwait(false),
            teams => GraphResults.Collection(teams.Select(TeamDto.From), "teams", odata.Query));
    }

    private static async Task<IResult> GetAsync(string teamId, ODataQuery odata, GetTeam useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(teamId, cancellationToken).ConfigureAwait(false),
            team => GraphResults.Entity(TeamDto.From(team), "teams", odata.Query.Select));

    /// <summary>Graph answers <c>202 Accepted</c> with the operation in <c>Location</c> and the team in <c>Content-Location</c>, and no body.</summary>
    private static async Task<IResult> CreateAsync(JsonObject? body, HttpResponse response, CreateTeam useCase, CancellationToken cancellationToken)
    {
        var creation = TeamRequests.Creation(body);
        if (!creation.IsSuccess)
        {
            return GraphResults.Error(creation.Error);
        }

        return GraphResults.Match(
            await useCase.ExecuteAsync(creation.Value, cancellationToken).ConfigureAwait(false),
            operation =>
            {
                response.Headers.ContentLocation = TeamPaths.Team(operation.TeamId);
                return TypedResults.Accepted(TeamPaths.Operation(operation.TeamId, operation.Id));
            });
    }

    private static async Task<IResult> GetOperationAsync(string teamId, string operationId, ODataQuery odata, GetTeamOperation useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(teamId, operationId, cancellationToken).ConfigureAwait(false),
            operation => GraphResults.Entity(TeamsAsyncOperationDto.From(operation), $"{TeamPaths.Context(teamId)}/operations", odata.Query.Select));

    private static async Task<IResult> UpdateAsync(string teamId, JsonObject? body, UpdateTeam useCase, CancellationToken cancellationToken)
    {
        var update = TeamRequests.Update(body);
        return update.IsSuccess
            ? GraphResults.Match(await useCase.ExecuteAsync(teamId, update.Value, cancellationToken).ConfigureAwait(false))
            : GraphResults.Error(update.Error);
    }

    private static async Task<IResult> DeleteAsync(string groupId, DeleteTeam useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.ExecuteAsync(groupId, cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> ListMembersAsync(string teamId, ODataQuery odata, ListTeamMembers useCase, CancellationToken cancellationToken) =>
        TeamRequests.WholeList(odata.Query) is { } invalid
            ? GraphResults.Error(invalid)
            : GraphResults.Match(
                await useCase.ExecuteAsync(teamId, cancellationToken).ConfigureAwait(false),
                members => GraphResults.Collection(members.Select(ConversationMemberDto.From), Members(teamId), odata.Query));

    private static async Task<IResult> GetMemberAsync(string teamId, string membershipId, ODataQuery odata, GetTeamMember useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(teamId, membershipId, cancellationToken).ConfigureAwait(false),
            member => GraphResults.Entity(ConversationMemberDto.From(member), Members(teamId), odata.Query.Select));

    private static async Task<IResult> AddMemberAsync(string teamId, JsonObject? body, AddTeamMember useCase, CancellationToken cancellationToken)
    {
        var member = ChatRequests.Member(body);
        if (!member.IsSuccess)
        {
            return GraphResults.Error(member.Error);
        }

        return GraphResults.Match(
            await useCase.ExecuteAsync(teamId, member.Value, cancellationToken).ConfigureAwait(false),
            added => GraphResults.Created(ConversationMemberDto.From(added), Members(teamId)));
    }

    private static async Task<IResult> UpdateMemberAsync(
        string teamId, string membershipId, JsonObject? body, ODataQuery odata, UpdateTeamMember useCase, CancellationToken cancellationToken)
    {
        var isOwner = TeamRequests.IsOwner(body);
        if (!isOwner.IsSuccess)
        {
            return GraphResults.Error(isOwner.Error);
        }

        return GraphResults.Match(
            await useCase.ExecuteAsync(teamId, membershipId, isOwner.Value, cancellationToken).ConfigureAwait(false),
            member => GraphResults.Entity(ConversationMemberDto.From(member), Members(teamId), odata.Query.Select));
    }

    private static async Task<IResult> RemoveMemberAsync(string teamId, string membershipId, RemoveTeamMember useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.ExecuteAsync(teamId, membershipId, cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> ListChannelsAsync(string teamId, ODataQuery odata, ListChannels useCase, CancellationToken cancellationToken) =>
        TeamRequests.WholeList(odata.Query) is { } invalid
            ? GraphResults.Error(invalid)
            : GraphResults.Match(
                await useCase.ExecuteAsync(teamId, cancellationToken).ConfigureAwait(false),
                channels => GraphResults.Collection(channels.Select(ChannelDto.From), Channels(teamId), odata.Query));

    private static async Task<IResult> GetChannelAsync(string teamId, string channelId, ODataQuery odata, GetChannel useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(teamId, channelId, cancellationToken).ConfigureAwait(false),
            channel => GraphResults.Entity(ChannelDto.From(channel), Channels(teamId), odata.Query.Select));

    private static async Task<IResult> GetPrimaryChannelAsync(string teamId, ODataQuery odata, GetPrimaryChannel useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(teamId, cancellationToken).ConfigureAwait(false),
            channel => GraphResults.Entity(ChannelDto.From(channel), $"{TeamPaths.Context(teamId)}/primaryChannel", odata.Query.Select));

    private static async Task<IResult> CreateChannelAsync(string teamId, JsonObject? body, CreateChannel useCase, CancellationToken cancellationToken)
    {
        var creation = TeamRequests.ChannelCreation(body);
        if (!creation.IsSuccess)
        {
            return GraphResults.Error(creation.Error);
        }

        return GraphResults.Match(
            await useCase.ExecuteAsync(teamId, creation.Value, cancellationToken).ConfigureAwait(false),
            channel => GraphResults.Created(ChannelDto.From(channel), Channels(teamId)));
    }

    private static async Task<IResult> UpdateChannelAsync(string teamId, string channelId, JsonObject? body, UpdateChannel useCase, CancellationToken cancellationToken)
    {
        var update = TeamRequests.ChannelUpdate(body);
        return update.IsSuccess
            ? GraphResults.Match(await useCase.ExecuteAsync(teamId, channelId, update.Value, cancellationToken).ConfigureAwait(false))
            : GraphResults.Error(update.Error);
    }

    private static async Task<IResult> DeleteChannelAsync(string teamId, string channelId, DeleteChannel useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.ExecuteAsync(teamId, channelId, cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> GetFilesFolderAsync(
        string teamId, string channelId, ODataQuery odata, IOptions<DiscoveryOptions> discovery, GetChannelFilesFolder useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(teamId, channelId, cancellationToken).ConfigureAwait(false),
            folder => GraphResults.Entity(
                DriveItemResourceDto.From(folder, discovery.Value.FilesBaseUrls?.FirstOrDefault()),
                $"{TeamPaths.Channel(teamId, channelId)}/filesFolder", odata.Query.Select));

    private static async Task<IResult> ListChannelMembersAsync(string teamId, string channelId, ODataQuery odata, ListChannelMembers useCase, CancellationToken cancellationToken) =>
        TeamRequests.WholeList(odata.Query) is { } invalid
            ? GraphResults.Error(invalid)
            : GraphResults.Match(
                await useCase.ExecuteAsync(teamId, channelId, cancellationToken).ConfigureAwait(false),
                members => GraphResults.Collection(members.Select(ConversationMemberDto.From), $"{TeamPaths.Channel(teamId, channelId)}/members", odata.Query));

    private static async Task<IResult> ListMessagesAsync(string teamId, string channelId, ODataQuery odata, ListChannelMessages useCase, CancellationToken cancellationToken)
    {
        var query = ChatRequests.PageQuery(odata.Query, "replies");
        if (!query.IsSuccess)
        {
            return GraphResults.Error(query.Error);
        }

        // Graph's page of channel messages is 20 unless $top says otherwise.
        query = query.Value with { Top = odata.Query.Top ?? TeamRequests.DefaultMessagePage };
        var withReplies = odata.Query.ExpandOf("replies") is not null;
        return GraphResults.Match(
            await useCase.ExecuteAsync(teamId, channelId, query.Value, withReplies, cancellationToken).ConfigureAwait(false),
            page => GraphResults.Page(page, ChannelPostDto.From, Messages(teamId, channelId), odata.Query));
    }

    private static async Task<IResult> SendMessageAsync(string teamId, string channelId, JsonObject? body, SendChannelMessage useCase, CancellationToken cancellationToken)
    {
        var draft = ChatRequests.Draft(body);
        if (!draft.IsSuccess)
        {
            return GraphResults.Error(draft.Error);
        }

        return GraphResults.Match(
            await useCase.ExecuteAsync(teamId, channelId, draft.Value, cancellationToken).ConfigureAwait(false),
            message => GraphResults.Created(ChatMessageDto.Of(message), Messages(teamId, channelId)));
    }

    private static async Task<IResult> DeltaAsync(
        string teamId, string channelId, HttpRequest request, ODataQuery odata, DeltaLinkCodec codec, GetChannelMessagesDelta useCase, CancellationToken cancellationToken)
    {
        if (odata.Query.Filter is not null || odata.Query.Expand.Count > 0)
        {
            return GraphResults.Error(Error.Validation("This delta takes $select, $top and its own links only."));
        }

        var cursor = codec.ReadCursor(request.Query, odata.Query);
        if (!cursor.IsSuccess)
        {
            return GraphResults.Error(cursor.Error);
        }

        return GraphResults.Match(
            await useCase.ExecuteAsync(teamId, channelId, cursor.Value.Token, cursor.Value.Top, cancellationToken).ConfigureAwait(false),
            page => GraphResults.Delta(page, ChatMessageDto.Of, cursor.Value, "chatMessage"));
    }

    private static async Task<IResult> GetMessageAsync(ChannelMessageRef message, ODataQuery odata, GetChannelMessage useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(message, cancellationToken).ConfigureAwait(false),
            found => GraphResults.Entity(ChatMessageDto.Of(found), MessagesOf(message), odata.Query.Select));

    private static async Task<IResult> UpdateMessageAsync(ChannelMessageRef message, JsonObject? body, UpdateChannelMessage useCase, CancellationToken cancellationToken)
    {
        var draft = ChatRequests.Draft(body);
        return draft.IsSuccess
            ? GraphResults.Match(await useCase.ExecuteAsync(message, draft.Value, cancellationToken).ConfigureAwait(false))
            : GraphResults.Error(draft.Error);
    }

    private static async Task<IResult> SoftDeleteAsync(ChannelMessageRef message, SoftDeleteChannelMessage useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.ExecuteAsync(message, cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> SetReactionAsync(ChannelMessageRef message, JsonObject? body, SetChannelMessageReaction useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.ExecuteAsync(message, ChatRequests.Text(body, "reactionType"), cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> UnsetReactionAsync(ChannelMessageRef message, JsonObject? body, UnsetChannelMessageReaction useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.ExecuteAsync(message, ChatRequests.Text(body, "reactionType"), cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> ListRepliesAsync(
        string teamId, string channelId, string messageId, ODataQuery odata, ListChannelMessageReplies useCase, CancellationToken cancellationToken)
    {
        var query = ChatRequests.PageQuery(odata.Query);
        if (!query.IsSuccess)
        {
            return GraphResults.Error(query.Error);
        }

        return GraphResults.Match(
            await useCase.ExecuteAsync(new ChannelMessageRef(teamId, channelId, messageId), query.Value, cancellationToken).ConfigureAwait(false),
            page => GraphResults.Page(page, ChatMessageDto.Of, Replies(teamId, channelId, messageId), odata.Query));
    }

    private static async Task<IResult> SendReplyAsync(
        string teamId, string channelId, string messageId, JsonObject? body, SendChannelMessageReply useCase, CancellationToken cancellationToken)
    {
        var draft = ChatRequests.Draft(body);
        if (!draft.IsSuccess)
        {
            return GraphResults.Error(draft.Error);
        }

        return GraphResults.Match(
            await useCase.ExecuteAsync(new ChannelMessageRef(teamId, channelId, messageId), draft.Value, cancellationToken).ConfigureAwait(false),
            reply => GraphResults.Created(ChatMessageDto.Of(reply), Replies(teamId, channelId, messageId)));
    }

    private static string Members(string teamId) => $"{TeamPaths.Context(teamId)}/members";

    private static string Channels(string teamId) => $"{TeamPaths.Context(teamId)}/channels";

    private static string Messages(string teamId, string channelId) => $"{TeamPaths.Channel(teamId, channelId)}/messages";

    private static string Replies(string teamId, string channelId, string messageId) => $"{TeamPaths.Message(teamId, channelId, messageId)}/replies";

    private static string MessagesOf(ChannelMessageRef message) =>
        message.ReplyId is null ? Messages(message.TeamId, message.ChannelId) : Replies(message.TeamId, message.ChannelId, message.MessageId);
}
