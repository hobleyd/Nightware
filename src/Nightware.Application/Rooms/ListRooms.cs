using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;
using Nightware.Domain.Rooms;

namespace Nightware.Application.Rooms;

/// <summary>Graph <c>GET places/microsoft.graph.room</c>: one page of the room directory.</summary>
public sealed class ListRooms(IRoomDirectory rooms)
{
    public Task<Result<Page<Room>>> ExecuteAsync(ItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return rooms.ListAsync(query, cancellationToken);
    }
}
