using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Directory;

/// <summary><c>GET /groups/{id}</c>: one group of the signed-in user's organizations.</summary>
public sealed class GetGroup(IGroupDirectory groups)
{
    public async Task<Result<DirectoryGroup>> ExecuteAsync(string groupId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        return groupId.Trim().Length == 0
            ? DirectoryObjects.ResourceNotFound(groupId)
            : DirectoryObjects.OrMiss(await groups.GetAsync(groupId.Trim(), cancellationToken).ConfigureAwait(false), groupId);
    }
}
