using Nightware.Application.Common.Identity;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Directory;

/// <summary><c>GET /me/manager</c>, <c>GET /users/{x}/manager</c>: the user's manager; 404 when none is set, as Graph's.</summary>
public sealed class GetManager(IUserContext user, IUserDirectory directory, IReportingLines lines)
{
    /// <param name="idOrUpn">Null for the signed-in user.</param>
    public async Task<Result<DirectoryUser>> ExecuteAsync(string? idOrUpn, CancellationToken cancellationToken)
    {
        var userId = await DirectoryObjects.UserIdAsync(user, directory, idOrUpn, cancellationToken).ConfigureAwait(false);
        if (!userId.IsSuccess)
        {
            return userId.Error;
        }

        return DirectoryObjects.OrMiss(await lines.GetManagerAsync(userId.Value, cancellationToken).ConfigureAwait(false), "manager");
    }
}
