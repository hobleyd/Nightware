using Nightware.Adapters.Directory.Core.Admin;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Administration;

/// <summary>
/// Re-resolves an administrator's scope from Keycloak on every call (ADR 0002 D1; the host caches it):
/// global = effective realm role <c>nightware-global-admin</c>; entity admin of each organization the user
/// is a member of whose <c>admins</c> group they belong to (<see cref="KeycloakEntityAdmins"/>).
/// </summary>
internal sealed class KeycloakAdminScopeSource(IKeycloakAdminClient keycloak, KeycloakEntityAdmins admins) : IAdminScopeSource
{
    public async Task<Result<AdminScope>> ResolveAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        Result<IReadOnlyList<KeycloakRole>> roles = await keycloak.GetEffectiveUserRealmRolesAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!roles.IsSuccess)
        {
            return roles.Error;
        }

        Result<IReadOnlyList<KeycloakOrganization>> organizations = await keycloak.ListMemberOrganizationsAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!organizations.IsSuccess)
        {
            return organizations.Error;
        }

        HashSet<string> entityIds = new(StringComparer.Ordinal);
        foreach (string organizationId in organizations.Value.Select(static o => o.Id).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            Result<bool> isAdmin = await admins.IsAdminAsync(organizationId, userId, cancellationToken).ConfigureAwait(false);
            if (!isAdmin.IsSuccess)
            {
                return isAdmin.Error;
            }

            if (isAdmin.Value)
            {
                entityIds.Add(organizationId);
            }
        }

        return new AdminScope(KeycloakIdentityAccounts.HasGlobalAdmin(roles.Value), entityIds);
    }
}
