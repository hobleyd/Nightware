using Nightware.Adapters.Dav.Chats.Talk;
using Nightware.Application.Teams;
using Nightware.Domain.Kernel;
using Nightware.Domain.Teams;

namespace Nightware.Adapters.Dav.Teams;

/// <summary>
/// <see cref="ITeamStore"/> on Nextcloud Teams (ADR 0009 D5): a team is a circle the caller is in; creating one makes the
/// circle (with its team folder), adds the members and makes the General channel; deleting one deletes its channels'
/// rooms first, because Talk keeps a room when its team goes.
/// </summary>
internal sealed class NextcloudTeamStore(NextcloudCircles circles, TalkChatClient talk, TalkIdentities identities) : ITeamStore
{
    /// <summary>The name of the channel made with every team.</summary>
    public const string GeneralChannelName = "General";

    public async Task<Result<IReadOnlyList<Team>>> ListJoinedAsync(CancellationToken cancellationToken)
    {
        var listed = await circles.ListAsync(cancellationToken).ConfigureAwait(false);
        return listed.IsSuccess ? listed.Value.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).Select(ToTeam).ToList() : listed.Error;
    }

    public async Task<Result<Team>> GetAsync(string teamId, CancellationToken cancellationToken)
    {
        var circle = await circles.GetAsync(teamId, cancellationToken).ConfigureAwait(false);
        return circle.IsSuccess ? ToTeam(circle.Value) : circle.Error;
    }

    public async Task<Result<Team>> CreateAsync(TeamCreation creation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(creation);

        // Everyone named must exist before anything is made, so a typo doesn't leave half a team behind.
        var members = new List<(string AccountName, bool IsOwner)>();
        foreach (var member in creation.Members)
        {
            var accountName = await identities.AccountNameOfAsync(member.User, cancellationToken).ConfigureAwait(false);
            if (!accountName.IsSuccess)
            {
                return accountName.Error;
            }

            members.Add((accountName.Value, member.IsOwner));
        }

        var created = await circles.CreateAsync(creation.DisplayName, cancellationToken).ConfigureAwait(false);
        if (!created.IsSuccess)
        {
            return created.Error;
        }

        var circle = created.Value;
        var filled = await FillAsync(circle, creation, members, cancellationToken).ConfigureAwait(false);
        if (!filled.IsSuccess)
        {
            // Best effort: a team that couldn't be finished is taken down again; the original error is what matters.
            await DeleteRoomsAndCircleAsync(circle.Id, cancellationToken).ConfigureAwait(false);
            return filled.Error!;
        }

        // The creation answer has no creation time; the listing has.
        var read = await circles.GetAsync(circle.Id, cancellationToken).ConfigureAwait(false);
        return ToTeam(read.IsSuccess ? read.Value : circle with { Description = creation.Description ?? string.Empty });
    }

    public async Task<Result> UpdateAsync(string teamId, TeamUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        var circle = await circles.GetAsync(teamId, cancellationToken).ConfigureAwait(false);
        if (!circle.IsSuccess)
        {
            return circle.Error;
        }

        if (update.DisplayName is { } name && name != circle.Value.Name)
        {
            var renamed = await circles.RenameAsync(teamId, name, cancellationToken).ConfigureAwait(false);
            if (!renamed.IsSuccess)
            {
                return renamed;
            }
        }

        if (update.Description is { } description && description != circle.Value.Description)
        {
            var described = await circles.DescribeAsync(teamId, description, cancellationToken).ConfigureAwait(false);
            if (!described.IsSuccess)
            {
                return described;
            }
        }

        if (update.Visibility is { } visibility && CircleConfig.With(circle.Value.Config, visibility) is var config && config != circle.Value.Config)
        {
            return await circles.ConfigureAsync(teamId, config, cancellationToken).ConfigureAwait(false);
        }

        return Result.Success();
    }

    public async Task<Result> DeleteAsync(string teamId, CancellationToken cancellationToken)
    {
        var circle = await circles.GetAsync(teamId, cancellationToken).ConfigureAwait(false);
        if (!circle.IsSuccess)
        {
            return circle.Error;
        }

        if (circle.Value.Caller?.Level != CircleLevel.Owner)
        {
            return Error.Forbidden("Only the team's owner can delete it.");
        }

        // Every channel must be the caller's to delete before any goes: a member may have made one the owner doesn't moderate.
        var tokens = await circles.RoomTokensAsync(teamId, cancellationToken).ConfigureAwait(false);
        if (!tokens.IsSuccess)
        {
            return tokens.Error;
        }

        if (tokens.Value.Count > 0)
        {
            var rooms = await talk.ListRoomsAsync(cancellationToken).ConfigureAwait(false);
            if (!rooms.IsSuccess)
            {
                return rooms.Error;
            }

            if (tokens.Value.Any(t => rooms.Value.FirstOrDefault(r => r.Token == t) is not { } room || !TalkParticipantType.Moderates(room.ParticipantType)))
            {
                return Error.Forbidden("A channel of the team is moderated by someone else, so the team can't be deleted by you.");
            }
        }

        return await DeleteRoomsAndCircleAsync(teamId, cancellationToken).ConfigureAwait(false);
    }

    internal static Team ToTeam(Circle circle)
    {
        ArgumentNullException.ThrowIfNull(circle);
        return new Team(circle.Id, circle.Name)
        {
            Description = circle.Description.Length == 0 ? null : circle.Description,
            Visibility = CircleConfig.VisibilityOf(circle.Config),
            CreatedDateTime = circle.Creation,
            WebUrl = circle.Url,
        };
    }

    private async Task<Result> FillAsync(Circle circle, TeamCreation creation, List<(string AccountName, bool IsOwner)> members, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(creation.Description))
        {
            var described = await circles.DescribeAsync(circle.Id, creation.Description, cancellationToken).ConfigureAwait(false);
            if (!described.IsSuccess)
            {
                return described;
            }
        }

        if (creation.Visibility is { } visibility && CircleConfig.With(circle.Config, visibility) is var config && config != circle.Config)
        {
            var configured = await circles.ConfigureAsync(circle.Id, config, cancellationToken).ConfigureAwait(false);
            if (!configured.IsSuccess)
            {
                return configured;
            }
        }

        foreach (var (accountName, isOwner) in members.DistinctBy(m => m.AccountName, StringComparer.Ordinal))
        {
            // Graph's examples name the caller among the members; the caller already owns the circle.
            if (accountName == circle.Caller?.UserId)
            {
                continue;
            }

            var added = await circles.AddUserAsync(circle.Id, accountName, cancellationToken).ConfigureAwait(false);
            if (!added.IsSuccess)
            {
                return added.Error;
            }

            if (isOwner)
            {
                var promoted = await circles.SetLevelAsync(circle.Id, added.Value.Id, CircleLevel.Admin, cancellationToken).ConfigureAwait(false);
                if (!promoted.IsSuccess)
                {
                    return promoted;
                }
            }
        }

        var general = await talk.CreateTeamRoomAsync(GeneralChannelName, null, circle.Id, cancellationToken).ConfigureAwait(false);
        return general.IsSuccess ? Result.Success() : general.Error;
    }

    private async Task<Result> DeleteRoomsAndCircleAsync(string teamId, CancellationToken cancellationToken)
    {
        var tokens = await circles.RoomTokensAsync(teamId, cancellationToken).ConfigureAwait(false);
        if (!tokens.IsSuccess)
        {
            return tokens.Error;
        }

        foreach (var token in tokens.Value)
        {
            var deleted = await talk.DeleteRoomAsync(token, cancellationToken).ConfigureAwait(false);
            if (!deleted.IsSuccess && deleted.Error!.Kind != ErrorKind.NotFound)
            {
                return deleted;
            }
        }

        return await circles.DestroyAsync(teamId, cancellationToken).ConfigureAwait(false);
    }
}
