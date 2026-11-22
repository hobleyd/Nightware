using Nightware.Application.Common.Query;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Directory;

/// <summary><c>GET /groups</c>: the groups of the signed-in user's organizations.</summary>
public sealed class ListGroups(IGroupDirectory groups)
{
    public Task<Result<Page<DirectoryGroup>>> ExecuteAsync(ItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return groups.ListAsync(query, cancellationToken);
    }
}
