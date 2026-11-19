using Nightware.Adapters.Directory.Core.Admin;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Administration;

/// <summary>
/// A top-level organization group the console keeps its objects under (<c>groups</c>, <c>mailboxes</c>), so
/// <c>admins</c> and any other system group is never read or edited as one of them. Created on first use.
/// </summary>
internal static class OrganizationParentGroup
{
    public static async Task<Result<KeycloakGroup?>> FindAsync(IKeycloakAdminClient keycloak, string entityId, string name, CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<KeycloakGroup>> found = await keycloak.ListOrganizationTopLevelGroupsAsync(entityId, cancellationToken).ConfigureAwait(false);
        return found.IsSuccess
            ? found.Value.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.Ordinal))
            : found.Error;
    }

    public static async Task<Result<string>> EnsureAsync(IKeycloakAdminClient keycloak, string entityId, string name, CancellationToken cancellationToken)
    {
        Result<KeycloakGroup?> existing = await FindAsync(keycloak, entityId, name, cancellationToken).ConfigureAwait(false);
        if (!existing.IsSuccess || existing.Value is not null)
        {
            return existing.IsSuccess ? existing.Value!.Id : existing.Error;
        }

        Result<string> created = await keycloak.CreateOrganizationGroupAsync(entityId, name, cancellationToken).ConfigureAwait(false);
        if (created.IsSuccess || created.Error.Kind != ErrorKind.Conflict)
        {
            return created;
        }

        // Another request made it first.
        Result<KeycloakGroup?> raced = await FindAsync(keycloak, entityId, name, cancellationToken).ConfigureAwait(false);
        return raced.IsSuccess && raced.Value is { } parent ? parent.Id : created;
    }
}
