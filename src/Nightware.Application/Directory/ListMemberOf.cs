using Nightware.Application.Common.Identity;
using Nightware.Application.Common.Query;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Directory;

/// <summary>
/// <c>GET /me/memberOf</c>, <c>GET /users/{x}/memberOf</c> and their <c>transitiveMemberOf</c> forms: the user's groups.
/// Groups don't nest, so direct and transitive membership are the same list.
/// </summary>
public sealed class ListMemberOf(IUserContext user, IUserDirectory directory, IGroupDirectory groups)
{
    /// <param name="idOrUpn">Null for the signed-in user.</param>
    public async Task<Result<Page<DirectoryGroup>>> ExecuteAsync(string? idOrUpn, ItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var userId = await DirectoryObjects.UserIdAsync(user, directory, idOrUpn, cancellationToken).ConfigureAwait(false);
        return userId.IsSuccess ? await groups.ListMemberOfAsync(userId.Value, query, cancellationToken).ConfigureAwait(false) : userId.Error;
    }
}
