using Nightware.Application.Files.Drives;
using Nightware.Domain.Chats;
using Nightware.Domain.Files;
using Nightware.Domain.Kernel;
using Nightware.Domain.Teams;

namespace Nightware.Application.Teams;

/// <summary>Checks on what teams and channels are given.</summary>
public static class TeamRules
{
    /// <summary>Graph's limit on a team's name.</summary>
    public const int MaxTeamNameLength = 256;

    /// <summary>Graph's limit on a team's or channel's description.</summary>
    public const int MaxDescriptionLength = 1024;

    private static readonly System.Buffers.SearchValues<char> ForbiddenInChannelNames = System.Buffers.SearchValues.Create(Channel.ForbiddenNameCharacters);

    public static Error? TeamName(string? name, bool required)
    {
        if (name is null)
        {
            return required ? Error.Validation("A team needs a displayName.", "displayName") : null;
        }

        return string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxTeamNameLength
            ? Error.Validation($"A team's displayName has 1 to {MaxTeamNameLength} characters.", "displayName")
            : null;
    }

    public static Error? Description(string? description) =>
        description is { Length: > MaxDescriptionLength }
            ? Error.Validation($"A description has at most {MaxDescriptionLength} characters.", "description")
            : null;

    /// <summary>
    /// A channel's name as Graph allows it: at most 50 characters, none of <c>~ # % &amp; * { } + / \ : &lt; &gt; ? | ' "</c>,
    /// not starting with <c>_</c> or <c>.</c> nor ending with <c>.</c>. It also names the channel's folder.
    /// </summary>
    public static Error? ChannelName(string? name, bool required)
    {
        if (name is null)
        {
            return required ? Error.Validation("A channel needs a displayName.", "displayName") : null;
        }

        var trimmed = name.Trim();
        return trimmed.Length is 0 or > Channel.MaxNameLength
               || trimmed.AsSpan().ContainsAny(ForbiddenInChannelNames)
               || trimmed.StartsWith('_') || trimmed.StartsWith('.') || trimmed.EndsWith('.')
               || !DriveItemRef.IsValidName(trimmed)
            ? Error.Validation(
                $"A channel's displayName has 1 to {Channel.MaxNameLength} characters, none of {Channel.ForbiddenNameCharacters}, and doesn't start with _ or . or end with a period.",
                "displayName")
            : null;
    }
}

/// <summary>Graph <c>GET /me/joinedTeams</c>.</summary>
public sealed class ListJoinedTeams(ITeamStore teams)
{
    public Task<Result<IReadOnlyList<Team>>> ExecuteAsync(CancellationToken cancellationToken) => teams.ListJoinedAsync(cancellationToken);
}

/// <summary>Graph <c>GET /teams/{id}</c>.</summary>
public sealed class GetTeam(ITeamStore teams)
{
    public Task<Result<Team>> ExecuteAsync(string teamId, CancellationToken cancellationToken) => teams.GetAsync(teamId, cancellationToken);
}

/// <summary>Graph <c>POST /teams</c> with the <c>standard</c> template: the team, its members and its General channel.</summary>
public sealed class CreateTeam(ITeamStore teams)
{
    public async Task<Result<TeamCreationOperation>> ExecuteAsync(TeamCreation creation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(creation);
        if ((TeamRules.TeamName(creation.DisplayName, required: true) ?? TeamRules.Description(creation.Description)) is { } invalid)
        {
            return invalid;
        }

        var created = await teams.CreateAsync(creation with { DisplayName = creation.DisplayName.Trim() }, cancellationToken).ConfigureAwait(false);
        return created.IsSuccess
            ? new TeamCreationOperation(TeamCreationOperation.IdFor(created.Value.Id), created.Value.Id, created.Value.CreatedDateTime)
            : created.Error;
    }
}

/// <summary>Graph <c>GET /teams/{id}/operations/{operationId}</c>: the (finished) creation of the team.</summary>
public sealed class GetTeamOperation(ITeamStore teams)
{
    public async Task<Result<TeamCreationOperation>> ExecuteAsync(string teamId, string operationId, CancellationToken cancellationToken)
    {
        if (TeamCreationOperation.TeamOf(operationId) != teamId)
        {
            return Error.NotFound("The operation was not found.");
        }

        var team = await teams.GetAsync(teamId, cancellationToken).ConfigureAwait(false);
        return team.IsSuccess ? new TeamCreationOperation(operationId, teamId, team.Value.CreatedDateTime) : team.Error;
    }
}

/// <summary>Graph <c>PATCH /teams/{id}</c>: name, description and visibility.</summary>
public sealed class UpdateTeam(ITeamStore teams)
{
    public async Task<Result> ExecuteAsync(string teamId, TeamUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        if ((TeamRules.TeamName(update.DisplayName, required: false) ?? TeamRules.Description(update.Description)) is { } invalid)
        {
            return invalid;
        }

        return update.IsEmpty
            ? Result.Success()
            : await teams.UpdateAsync(teamId, update with { DisplayName = update.DisplayName?.Trim() }, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Graph <c>DELETE /groups/{id}</c> for a team's group. Any other group (a directory group, ADR 0009 D2) is read-only
/// here, so it is <see cref="ErrorKind.Unsupported"/>.
/// </summary>
public sealed class DeleteTeam(ITeamStore teams)
{
    public async Task<Result> ExecuteAsync(string groupId, CancellationToken cancellationToken) =>
        Team.IsTeamId(groupId)
            ? await teams.DeleteAsync(groupId, cancellationToken).ConfigureAwait(false)
            : Error.Unsupported("Only a team's group can be deleted here; directory groups are managed in the console.");
}

/// <summary>Graph <c>GET /teams/{id}/members</c>.</summary>
public sealed class ListTeamMembers(ITeamMembers members)
{
    public Task<Result<IReadOnlyList<ChatMember>>> ExecuteAsync(string teamId, CancellationToken cancellationToken) =>
        members.ListAsync(teamId, cancellationToken);
}

/// <summary>Graph <c>GET /teams/{id}/members/{membershipId}</c>.</summary>
public sealed class GetTeamMember(ITeamMembers members)
{
    public Task<Result<ChatMember>> ExecuteAsync(string teamId, string membershipId, CancellationToken cancellationToken) =>
        members.GetAsync(teamId, membershipId, cancellationToken);
}

/// <summary>Graph <c>POST /teams/{id}/members</c>.</summary>
public sealed class AddTeamMember(ITeamMembers members)
{
    public Task<Result<ChatMember>> ExecuteAsync(string teamId, ChatMemberDraft member, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(member);
        return members.AddAsync(teamId, member, cancellationToken);
    }
}

/// <summary>Graph <c>PATCH /teams/{id}/members/{membershipId}</c>: <c>roles</c> <c>["owner"]</c> or <c>[]</c>.</summary>
public sealed class UpdateTeamMember(ITeamMembers members)
{
    public Task<Result<ChatMember>> ExecuteAsync(string teamId, string membershipId, bool isOwner, CancellationToken cancellationToken) =>
        members.SetOwnerAsync(teamId, membershipId, isOwner, cancellationToken);
}

/// <summary>Graph <c>DELETE /teams/{id}/members/{membershipId}</c>.</summary>
public sealed class RemoveTeamMember(ITeamMembers members)
{
    public Task<Result> ExecuteAsync(string teamId, string membershipId, CancellationToken cancellationToken) =>
        members.RemoveAsync(teamId, membershipId, cancellationToken);
}

/// <summary>Graph <c>GET /teams/{id}/channels</c>.</summary>
public sealed class ListChannels(IChannelStore channels)
{
    public Task<Result<IReadOnlyList<Channel>>> ExecuteAsync(string teamId, CancellationToken cancellationToken) =>
        channels.ListAsync(teamId, cancellationToken);
}

/// <summary>Graph <c>GET /teams/{id}/channels/{channelId}</c>.</summary>
public sealed class GetChannel(IChannelStore channels)
{
    public Task<Result<Channel>> ExecuteAsync(string teamId, string channelId, CancellationToken cancellationToken) =>
        channels.GetAsync(teamId, channelId, cancellationToken);
}

/// <summary>Graph <c>GET /teams/{id}/primaryChannel</c>.</summary>
public sealed class GetPrimaryChannel(IChannelStore channels)
{
    public Task<Result<Channel>> ExecuteAsync(string teamId, CancellationToken cancellationToken) =>
        channels.GetPrimaryAsync(teamId, cancellationToken);
}

/// <summary>Graph <c>POST /teams/{id}/channels</c>: a standard channel.</summary>
public sealed class CreateChannel(IChannelStore channels)
{
    public async Task<Result<Channel>> ExecuteAsync(string teamId, ChannelCreation creation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(creation);
        if ((TeamRules.ChannelName(creation.DisplayName, required: true) ?? TeamRules.Description(creation.Description)) is { } invalid)
        {
            return invalid;
        }

        return await channels.CreateAsync(teamId, creation with { DisplayName = creation.DisplayName.Trim() }, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Graph <c>PATCH /teams/{id}/channels/{channelId}</c>: name and description.</summary>
public sealed class UpdateChannel(IChannelStore channels)
{
    public async Task<Result> ExecuteAsync(string teamId, string channelId, ChannelUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        if ((TeamRules.ChannelName(update.DisplayName, required: false) ?? TeamRules.Description(update.Description)) is { } invalid)
        {
            return invalid;
        }

        return update.IsEmpty
            ? Result.Success()
            : await channels.UpdateAsync(teamId, channelId, update with { DisplayName = update.DisplayName?.Trim() }, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Graph <c>DELETE /teams/{id}/channels/{channelId}</c>.</summary>
public sealed class DeleteChannel(IChannelStore channels)
{
    public Task<Result> ExecuteAsync(string teamId, string channelId, CancellationToken cancellationToken) =>
        channels.DeleteAsync(teamId, channelId, cancellationToken);
}

/// <summary>Graph <c>GET /teams/{id}/channels/{channelId}/members</c>: a standard channel's members are the team's.</summary>
public sealed class ListChannelMembers(IChannelStore channels, ITeamMembers members)
{
    public async Task<Result<IReadOnlyList<ChatMember>>> ExecuteAsync(string teamId, string channelId, CancellationToken cancellationToken)
    {
        var channel = await channels.GetAsync(teamId, channelId, cancellationToken).ConfigureAwait(false);
        return channel.IsSuccess ? await members.ListAsync(teamId, cancellationToken).ConfigureAwait(false) : channel.Error;
    }
}

/// <summary>
/// Graph <c>GET /teams/{id}/channels/{channelId}/filesFolder</c>: the channel's folder in the team's folder, created on
/// first use (ADR 0009 D5). It is read through the drive, so it is the caller's drive item like any other (ADR 0008).
/// </summary>
public sealed class GetChannelFilesFolder(IChannelStore channels, IDriveStore drive)
{
    public async Task<Result<DriveItem>> ExecuteAsync(string teamId, string channelId, CancellationToken cancellationToken)
    {
        var path = await channels.FilesFolderPathAsync(teamId, channelId, cancellationToken).ConfigureAwait(false);
        if (!path.IsSuccess)
        {
            return path.Error;
        }

        if (path.Value.Count == 0 || !path.Value.All(DriveItemRef.IsValidName))
        {
            return Error.BackendUnavailable("The team's folder has no usable path.");
        }

        var folder = new DriveItemRef(null, path.Value);
        var existing = await drive.GetItemAsync(folder, cancellationToken).ConfigureAwait(false);
        if (existing.IsSuccess || existing.Error.Kind != ErrorKind.NotFound)
        {
            return existing;
        }

        var created = await drive.CreateFolderAsync(new DriveItemRef(null, path.Value.Take(path.Value.Count - 1).ToList()), path.Value[^1], ConflictBehavior.Fail, cancellationToken)
            .ConfigureAwait(false);

        // Someone else made it in between: theirs is the channel's folder.
        return created.IsSuccess || created.Error.Kind != ErrorKind.Conflict
            ? created
            : await drive.GetItemAsync(folder, cancellationToken).ConfigureAwait(false);
    }
}
