using Nightware.Adapters.Directory.Core;
using Nightware.Application.Subscriptions;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Subscriptions;

/// <summary>
/// <see cref="ISubscriberSessions"/> by Keycloak's refresh grant (ADR 0006 D10). The access token it returns carries
/// <c>aud [stalwart, nextcloud]</c> and exchanges for either backend's token like a request's. An ended session is
/// <see cref="ErrorKind.BackendAuthFailed"/>.
/// </summary>
internal sealed class KeycloakSubscriberSessions(IKeycloakTokenClient tokens) : ISubscriberSessions
{
    public async Task<Result<SubscriberSession>> RenewAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var pair = await tokens.RefreshAsync(refreshToken, cancellationToken).ConfigureAwait(false);
        return pair.IsSuccess ? new SubscriberSession(pair.Value.Access.Value, pair.Value.RefreshToken) : pair.Error;
    }
}
