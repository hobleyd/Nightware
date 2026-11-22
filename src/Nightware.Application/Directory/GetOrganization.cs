using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Directory;

/// <summary><c>GET /organization/{id}</c>: one of the signed-in user's organizations; any other id is a 404.</summary>
public sealed class GetOrganization(IOrganizationDirectory organizations)
{
    public async Task<Result<DirectoryOrganization>> ExecuteAsync(string organizationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        var mine = await organizations.ListAsync(cancellationToken).ConfigureAwait(false);
        if (!mine.IsSuccess)
        {
            return mine.Error;
        }

        var key = organizationId.Trim();
        return mine.Value.FirstOrDefault(o => string.Equals(o.Id, key, StringComparison.OrdinalIgnoreCase)) is { } found
            ? found
            : DirectoryObjects.ResourceNotFound(organizationId);
    }
}
