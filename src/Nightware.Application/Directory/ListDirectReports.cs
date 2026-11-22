using Nightware.Application.Common.Identity;
using Nightware.Application.Common.Query;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Directory;

/// <summary><c>GET /me/directReports</c>, <c>GET /users/{x}/directReports</c>: the people whose manager the user is.</summary>
public sealed class ListDirectReports(IUserContext user, IUserDirectory directory, IReportingLines lines)
{
    /// <param name="idOrUpn">Null for the signed-in user.</param>
    public async Task<Result<Page<DirectoryUser>>> ExecuteAsync(string? idOrUpn, ItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var userId = await DirectoryObjects.UserIdAsync(user, directory, idOrUpn, cancellationToken).ConfigureAwait(false);
        return userId.IsSuccess ? await lines.ListDirectReportsAsync(userId.Value, query, cancellationToken).ConfigureAwait(false) : userId.Error;
    }
}
