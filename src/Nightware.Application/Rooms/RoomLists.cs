using System.Text;
using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;
using Nightware.Domain.Rooms;

namespace Nightware.Application.Rooms;

/// <summary>
/// Graph room lists from the room directory (phase 19). Nextcloud has no room lists, only each room's building, so a
/// list is a building: every room naming it, under the building's name and street address.
/// </summary>
/// <remarks>
/// Graph names a room list by its email address (<c>places/{address}/microsoft.graph.roomlist/rooms</c>), and a
/// building has none. Its address is <c>{slug}@roomlists.invalid</c>: stable, readable, and in the reserved
/// <c>.invalid</c> domain (RFC 2606) so nothing mistakes it for a mailbox. Rooms with no building are in no list.
/// </remarks>
public static class RoomLists
{
    public const string Domain = "roomlists.invalid";

    /// <summary>Every room, walking the directory's pages (as <see cref="FindRooms"/> does).</summary>
    internal static async Task<Result<List<Room>>> AllRoomsAsync(IRoomDirectory rooms, CancellationToken cancellationToken)
    {
        var found = new List<Room>();
        string? skipToken = null;
        for (var page = 0; page < FindRooms.MaxPages; page++)
        {
            var result = await rooms.ListAsync(new ItemQuery { Top = FindRooms.PageSize, SkipToken = skipToken }, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return result.Error;
            }

            found.AddRange(result.Value.Items);
            skipToken = result.Value.SkipToken;
            if (skipToken is null)
            {
                break;
            }
        }

        return found;
    }

    /// <summary>The buildings as room lists, by name.</summary>
    internal static IReadOnlyList<RoomList> From(IEnumerable<Room> rooms) =>
        [.. rooms.Where(static r => !string.IsNullOrWhiteSpace(r.Building))
            .GroupBy(static r => Slug(r.Building!), StringComparer.Ordinal)
            .Where(static g => g.Key.Length > 0)
            .Select(static g => new RoomList("roomlist-" + g.Key, g.First().Building!.Trim(), $"{g.Key}@{Domain}")
            {
                Address = g.Select(static r => r.BuildingAddress).FirstOrDefault(static a => !string.IsNullOrWhiteSpace(a)),
            })
            .OrderBy(static l => l.DisplayName, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>The building name as an address's local part: lower-case letters and digits, runs of anything else as one '-'.</summary>
    internal static string Slug(string building)
    {
        var slug = new StringBuilder(building.Length);
        foreach (var c in building.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                slug.Append(c);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        return slug.ToString().TrimEnd('-');
    }
}

/// <summary>Graph <c>GET places/microsoft.graph.roomList</c>: the buildings, as room lists.</summary>
public sealed class ListRoomLists(IRoomDirectory rooms)
{
    public async Task<Result<IReadOnlyList<RoomList>>> ExecuteAsync(CancellationToken cancellationToken)
    {
        var all = await RoomLists.AllRoomsAsync(rooms, cancellationToken).ConfigureAwait(false);
        return all.IsSuccess ? Result<IReadOnlyList<RoomList>>.Success(RoomLists.From(all.Value)) : all.Error;
    }
}

/// <summary>Graph <c>GET places/{roomListAddress}/microsoft.graph.roomlist/rooms</c>: the rooms in one building.</summary>
public sealed class ListRoomsInList(IRoomDirectory rooms)
{
    public async Task<Result<IReadOnlyList<Room>>> ExecuteAsync(string roomListAddress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roomListAddress);
        var at = roomListAddress.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || !string.Equals(roomListAddress[(at + 1)..], RoomLists.Domain, StringComparison.OrdinalIgnoreCase))
        {
            return Error.NotFound($"There is no room list '{roomListAddress}'.");
        }

        var slug = roomListAddress[..at].ToLowerInvariant();
        var all = await RoomLists.AllRoomsAsync(rooms, cancellationToken).ConfigureAwait(false);
        if (!all.IsSuccess)
        {
            return all.Error;
        }

        var inList = all.Value.Where(r => r.Building is { } b && RoomLists.Slug(b) == slug).ToList();
        return inList.Count > 0 ? inList : Error.NotFound($"There is no room list '{roomListAddress}'.");
    }
}
