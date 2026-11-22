using Nightware.Adapters.Directory.Core.Admin;
using Nightware.Application.Common.Identity;
using Nightware.Application.Directory;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Directory;

/// <summary>
/// <see cref="IOrganizationDirectory"/>: the Keycloak organizations (entities, ADR 0002) the signed-in user is a member of,
/// read as the <c>nightware-api</c> service account. A disabled organization is left out. Its domains are in name order,
/// so the first, the default, is the same for everyone.
/// </summary>
internal sealed class KeycloakOrganizationDirectory(IKeycloakAdminClient keycloak, IUserContext user) : IOrganizationDirectory
{
    public async Task<Result<IReadOnlyList<DirectoryOrganization>>> ListAsync(CancellationToken cancellationToken)
    {
        var organizations = await keycloak.ListMemberOrganizationsAsync(user.Principal.Id, cancellationToken).ConfigureAwait(false);
        if (!organizations.IsSuccess)
        {
            // A caller Keycloak doesn't know belongs to no organization.
            return organizations.Error.Kind == ErrorKind.NotFound ? Array.Empty<DirectoryOrganization>() : organizations.Error;
        }

        return organizations.Value
            .Where(static o => o.Enabled && o.Id is not null)
            .Select(ToOrganization)
            .ToList();
    }

    internal static DirectoryOrganization ToOrganization(KeycloakOrganization organization)
    {
        ArgumentNullException.ThrowIfNull(organization);
        IReadOnlyList<string> domains = [.. (organization.Domains ?? [])
            .Select(static d => d.Name.Trim().ToLowerInvariant())
            .Where(static d => d.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
        return new DirectoryOrganization(organization.Id!, organization.Name, domains);
    }
}
