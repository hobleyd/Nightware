using System.Text.Json.Nodes;

using Nightware.Api.Features.Chats.Dto;
using Nightware.Api.Graph.Json;
using Nightware.Application.Teams;
using Nightware.Domain.Teams;

namespace Nightware.Api.Features.Teams.Dto;

/// <summary>
/// Graph <c>team</c> (ADR 0009 D5). <c>tenantId</c> is left out (there is no tenant, as for chats), and so are the
/// settings objects and <c>summary</c>, which Nextcloud has no counterpart for.
/// </summary>
public sealed record TeamDto
{
    public required string Id { get; init; }

    public DateTimeOffset? CreatedDateTime { get; init; }

    public required string DisplayName { get; init; }

    public string? Description { get; init; }

    public string? InternalId { get; init; }

    public string? Classification { get; init; }

    public string? Specialization { get; init; }

    public required string Visibility { get; init; }

    public string? WebUrl { get; init; }

    /// <summary>Nextcloud can't archive a team.</summary>
    public bool IsArchived { get; init; }

    public bool? IsMembershipLimitedToOwners { get; init; }

    public static TeamDto From(Team team)
    {
        ArgumentNullException.ThrowIfNull(team);
        return new TeamDto
        {
            Id = team.Id,
            CreatedDateTime = team.CreatedDateTime,
            DisplayName = team.DisplayName,
            Description = team.Description,
            Visibility = team.Visibility == TeamVisibility.Public ? "public" : "private",
            WebUrl = team.WebUrl,
        };
    }
}

/// <summary>Graph <c>teamsAsyncOperation</c>: a team's creation, which is finished before it is reported.</summary>
public sealed record TeamsAsyncOperationDto
{
    public required string Id { get; init; }

    public string OperationType { get; init; } = "createTeam";

    public DateTimeOffset? CreatedDateTime { get; init; }

    public string Status { get; init; } = "succeeded";

    public DateTimeOffset? LastActionDateTime { get; init; }

    public int AttemptsCount { get; init; } = 1;

    public required string TargetResourceId { get; init; }

    public required string TargetResourceLocation { get; init; }

    public object? Error { get; init; }

    public static TeamsAsyncOperationDto From(TeamCreationOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return new TeamsAsyncOperationDto
        {
            Id = operation.Id,
            CreatedDateTime = operation.CreatedDateTime,
            LastActionDateTime = operation.CreatedDateTime,
            TargetResourceId = operation.TeamId,
            TargetResourceLocation = TeamPaths.Team(operation.TeamId),
        };
    }
}

/// <summary>Graph <c>channel</c>: always a standard channel. Talk keeps no room creation time and no channel address.</summary>
public sealed record ChannelDto
{
    public required string Id { get; init; }

    public DateTimeOffset? CreatedDateTime { get; init; }

    public required string DisplayName { get; init; }

    public string? Description { get; init; }

    public bool? IsFavoriteByDefault { get; init; }

    /// <summary>Talk rooms have no mail address (ADR 0009 D5).</summary>
    public string Email { get; init; } = string.Empty;

    public string? WebUrl { get; init; }

    public string MembershipType { get; init; } = Channel.StandardMembershipType;

    /// <summary>A channel is posts and replies (Talk threads).</summary>
    public string LayoutType { get; init; } = "post";

    public bool IsArchived { get; init; }

    public static ChannelDto From(Channel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return new ChannelDto
        {
            Id = channel.Id,
            CreatedDateTime = channel.CreatedDateTime,
            DisplayName = channel.DisplayName,
            Description = channel.Description,
            WebUrl = channel.WebUrl,
        };
    }
}

/// <summary>A channel post, with its <c>replies</c> when <c>$expand=replies</c> asked for them.</summary>
public static class ChannelPostDto
{
    public static object From(ChannelPostView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var post = ChatMessageDto.Of(view.Post);
        if (view.Replies is not { } replies)
        {
            return post;
        }

        var json = GraphJson.ToObject(post);
        json["replies"] = new JsonArray(replies.Select(r => (JsonNode)GraphJson.ToObject(ChatMessageDto.Of(r))).ToArray());
        return json;
    }
}

/// <summary>Graph's paths and <c>@odata.context</c> fragments for teams and channels.</summary>
public static class TeamPaths
{
    public static string Team(string teamId) => $"/teams('{Uri.EscapeDataString(teamId)}')";

    public static string Operation(string teamId, string operationId) => $"{Team(teamId)}/operations('{Uri.EscapeDataString(operationId)}')";

    public static string Context(string teamId) => Team(teamId).TrimStart('/');

    public static string Channel(string teamId, string channelId) => $"{Context(teamId)}/channels('{Uri.EscapeDataString(channelId)}')";

    public static string Message(string teamId, string channelId, string messageId) =>
        $"{Channel(teamId, channelId)}/messages('{Uri.EscapeDataString(messageId)}')";
}
