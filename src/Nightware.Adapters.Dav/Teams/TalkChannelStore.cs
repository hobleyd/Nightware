using Nightware.Adapters.Dav.Chats.Talk;
using Nightware.Application.Teams;
using Nightware.Domain.Chats;
using Nightware.Domain.Kernel;
using Nightware.Domain.Teams;

namespace Nightware.Adapters.Dav.Teams;

/// <summary>
/// <see cref="IChannelStore"/> on Talk (ADR 0009 D5): a team's channels are its <c>talk</c> resources, group rooms whose
/// participants include the team. The primary channel is the one with the lowest Talk room id (General, made with the
/// team). Talk keeps no room creation time, so <c>createdDateTime</c> is null.
/// </summary>
internal sealed class TalkChannelStore(NextcloudCircles circles, TalkChatClient talk, TalkLinks links) : IChannelStore
{
    public async Task<Result<IReadOnlyList<Channel>>> ListAsync(string teamId, CancellationToken cancellationToken)
    {
        var rooms = await RoomsAsync(teamId, cancellationToken).ConfigureAwait(false);
        if (!rooms.IsSuccess)
        {
            return rooms.Error;
        }

        var primary = PrimaryOf(rooms.Value);
        return rooms.Value
            .OrderBy(r => r.Id != primary)
            .ThenBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(r => ToChannel(r, r.Id == primary))
            .ToList();
    }

    public async Task<Result<Channel>> GetAsync(string teamId, string channelId, CancellationToken cancellationToken)
    {
        var rooms = await RoomsAsync(teamId, cancellationToken).ConfigureAwait(false);
        if (!rooms.IsSuccess)
        {
            return rooms.Error;
        }

        return rooms.Value.FirstOrDefault(r => r.Token == channelId) is { } room ? ToChannel(room, room.Id == PrimaryOf(rooms.Value)) : NoChannel();
    }

    public async Task<Result<Channel>> GetPrimaryAsync(string teamId, CancellationToken cancellationToken)
    {
        var rooms = await RoomsAsync(teamId, cancellationToken).ConfigureAwait(false);
        if (!rooms.IsSuccess)
        {
            return rooms.Error;
        }

        var primary = PrimaryOf(rooms.Value);
        return rooms.Value.FirstOrDefault(r => r.Id == primary) is { } room ? ToChannel(room, true) : NoChannel();
    }

    public async Task<Result<Channel>> CreateAsync(string teamId, ChannelCreation creation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(creation);
        var rooms = await RoomsAsync(teamId, cancellationToken).ConfigureAwait(false);
        if (!rooms.IsSuccess)
        {
            return rooms.Error;
        }

        if (rooms.Value.Any(r => string.Equals(r.DisplayName, creation.DisplayName, StringComparison.OrdinalIgnoreCase)))
        {
            return NameTaken(creation.DisplayName);
        }

        var created = await talk.CreateTeamRoomAsync(creation.DisplayName, creation.Description, teamId, cancellationToken).ConfigureAwait(false);
        return created.IsSuccess ? ToChannel(created.Value, rooms.Value.Count == 0) : created.Error;
    }

    public async Task<Result> UpdateAsync(string teamId, string channelId, ChannelUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        var rooms = await RoomsAsync(teamId, cancellationToken).ConfigureAwait(false);
        if (!rooms.IsSuccess)
        {
            return rooms.Error;
        }

        if (rooms.Value.FirstOrDefault(r => r.Token == channelId) is not { } room)
        {
            return NoChannel();
        }

        if (update.DisplayName is { } name && name != room.DisplayName)
        {
            if (room.Id == PrimaryOf(rooms.Value))
            {
                return Error.Validation("The team's General channel can't be renamed.", "displayName");
            }

            if (rooms.Value.Any(r => r.Token != channelId && string.Equals(r.DisplayName, name, StringComparison.OrdinalIgnoreCase)))
            {
                return NameTaken(name);
            }

            var renamed = await talk.RenameRoomAsync(channelId, name, cancellationToken).ConfigureAwait(false);
            if (!renamed.IsSuccess)
            {
                return renamed;
            }
        }

        return update.Description is { } description && description != room.Description
            ? await talk.SetDescriptionAsync(channelId, description, cancellationToken).ConfigureAwait(false)
            : Result.Success();
    }

    public async Task<Result> DeleteAsync(string teamId, string channelId, CancellationToken cancellationToken)
    {
        var channel = await GetAsync(teamId, channelId, cancellationToken).ConfigureAwait(false);
        if (!channel.IsSuccess)
        {
            return channel.Error;
        }

        return channel.Value.IsPrimary
            ? Error.Validation("The team's General channel can't be deleted.")
            : await talk.DeleteRoomAsync(channelId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<IReadOnlyList<string>>> FilesFolderPathAsync(string teamId, string channelId, CancellationToken cancellationToken)
    {
        var channel = await GetAsync(teamId, channelId, cancellationToken).ConfigureAwait(false);
        if (!channel.IsSuccess)
        {
            return channel.Error;
        }

        var mountPoint = await circles.FolderMountPointAsync(teamId, cancellationToken).ConfigureAwait(false);
        if (!mountPoint.IsSuccess)
        {
            return mountPoint.Error;
        }

        if (mountPoint.Value is not { } folder)
        {
            return Error.NotFound("The team has no team folder for its channels' files.");
        }

        return folder.Split('/', StringSplitOptions.RemoveEmptyEntries).Append(FolderName(channel.Value.DisplayName)).ToList();
    }

    /// <summary>
    /// The channel's folder name: its name, with what a file name can't hold replaced (a channel made in Talk's own
    /// client may have a <c>/</c> in its name).
    /// </summary>
    internal static string FolderName(string displayName)
    {
        var name = new string(displayName.Trim().Select(c => c is '/' or '\\' || char.IsControl(c) ? '_' : c).ToArray());
        return name is "" or "." or ".." ? "_" : name;
    }

    /// <summary>
    /// The team's channel rooms the caller can see (its <c>talk</c> resources, in the caller's room list). The team is
    /// checked to be the caller's first: the circles app throttles requests for circles one isn't in.
    /// </summary>
    internal async Task<Result<IReadOnlyList<TalkRoom>>> RoomsAsync(string teamId, CancellationToken cancellationToken)
    {
        var circle = await circles.GetAsync(teamId, cancellationToken).ConfigureAwait(false);
        if (!circle.IsSuccess)
        {
            return circle.Error;
        }

        var tokens = await circles.RoomTokensAsync(teamId, cancellationToken).ConfigureAwait(false);
        if (!tokens.IsSuccess)
        {
            return tokens.Error;
        }

        if (tokens.Value.Count == 0)
        {
            return Array.Empty<TalkRoom>();
        }

        var rooms = await talk.ListRoomsAsync(cancellationToken).ConfigureAwait(false);
        if (!rooms.IsSuccess)
        {
            return rooms.Error;
        }

        var teamTokens = tokens.Value.ToHashSet(StringComparer.Ordinal);
        return rooms.Value.Where(r => teamTokens.Contains(r.Token) && !r.IsOneToOne).ToList();
    }

    /// <summary>One of the team's rooms, as a conversation whose messages are channel messages.</summary>
    internal async Task<Result<TalkConversation>> ConversationAsync(string teamId, string channelId, CancellationToken cancellationToken)
    {
        if (!TalkIds.IsToken(channelId))
        {
            return NoChannel();
        }

        var circle = await circles.GetAsync(teamId, cancellationToken).ConfigureAwait(false);
        if (!circle.IsSuccess)
        {
            return circle.Error;
        }

        var tokens = await circles.RoomTokensAsync(teamId, cancellationToken).ConfigureAwait(false);
        if (!tokens.IsSuccess)
        {
            return tokens.Error;
        }

        if (!tokens.Value.Contains(channelId))
        {
            return NoChannel();
        }

        var room = await talk.GetRoomAsync(channelId, cancellationToken).ConfigureAwait(false);
        if (!room.IsSuccess)
        {
            return room.Error.Kind == ErrorKind.NotFound ? NoChannel() : room.Error;
        }

        return new TalkConversation(channelId, room.Value.DisplayName)
        {
            Channel = new ChannelIdentity(teamId, channelId),
            WebUrl = links.ConversationUrl(channelId),
        };
    }

    internal static Error NoChannel() => Error.NotFound("The channel was not found in this team.");

    private Channel ToChannel(TalkRoom room, bool isPrimary) => new(room.Token, room.DisplayName.Length > 0 ? room.DisplayName : room.Name)
    {
        Description = room.Description.Length == 0 ? null : room.Description,
        WebUrl = links.ConversationUrl(room.Token),
        IsPrimary = isPrimary,
    };

    private static long PrimaryOf(IReadOnlyList<TalkRoom> rooms) => rooms.Count == 0 ? 0 : rooms.Min(r => r.Id);

    private static Error NameTaken(string name) =>
        new(ErrorKind.Conflict, "NameAlreadyExists", $"The team already has a channel named '{name}'.");
}
