using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;
using Nightware.Domain.Rooms;

namespace Nightware.Application.Rooms;

public interface IRoomDirectory
{
    Task<Result<Page<Room>>> ListAsync(ItemQuery query, CancellationToken cancellationToken);
}
