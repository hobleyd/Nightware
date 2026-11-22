using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Directory;

/// <summary><c>GET /organization</c>: the organizations (entities) the signed-in user belongs to; usually one.</summary>
public sealed class ListOrganizations(IOrganizationDirectory organizations)
{
    public Task<Result<IReadOnlyList<DirectoryOrganization>>> ExecuteAsync(CancellationToken cancellationToken) =>
        organizations.ListAsync(cancellationToken);
}
