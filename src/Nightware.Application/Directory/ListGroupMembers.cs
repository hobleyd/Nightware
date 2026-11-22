using Nightware.Application.Common.Query;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Directory;

/// <summary>
/// <c>GET /groups/{id}/members</c> and <c>…/transitiveMembers</c>: the group's people. Groups hold only people, so the two
/// are the same list.
/// </summary>
public sealed class ListGroupMembers(IGroupDirectory groups)
{
    public async Task<Result<Page<DirectoryUser>>> ExecuteAsync(string groupId, ItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        ArgumentNullException.ThrowIfNull(query);
        return groupId.Trim().Length == 0
            ? DirectoryObjects.ResourceNotFound(groupId)
            : DirectoryObjects.OrMiss(await groups.ListMembersAsync(groupId.Trim(), query, cancellationToken).ConfigureAwait(false), groupId);
    }
}
