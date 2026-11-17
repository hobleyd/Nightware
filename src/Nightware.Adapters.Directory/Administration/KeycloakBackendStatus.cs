using Nightware.Adapters.Directory.Core;
using Nightware.Adapters.Directory.Core.Admin;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Administration;

/// <summary>
/// Keycloak readiness for the console's dashboard: the service-account token can be obtained, and a cheap
/// admin call (<c>GET /users/count</c>) succeeds with it.
/// </summary>
internal sealed class KeycloakBackendStatus(IKeycloakTokenClient tokens, IKeycloakAdminClient keycloak) : IBackendStatus
{
    public async Task<BackendHealth> CheckAsync(CancellationToken cancellationToken)
    {
        Result<AccessToken> token = await tokens.GetServiceAccountTokenAsync(cancellationToken).ConfigureAwait(false);
        if (!token.IsSuccess)
        {
            return new BackendHealth(BackendKind.Identity, false, "The console cannot obtain its Keycloak service-account token.");
        }

        Result<int> users = await keycloak.CountUsersAsync(new KeycloakUserQuery(), cancellationToken).ConfigureAwait(false);
        return users.IsSuccess
            ? new BackendHealth(BackendKind.Identity, true)
            : new BackendHealth(BackendKind.Identity, false, $"The Keycloak admin API refused a read: {users.Error.Message}");
    }
}
