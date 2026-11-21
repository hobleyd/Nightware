using Nightware.Adapters.Directory.Core;
using Nightware.Application.Common.Identity;
using Nightware.Application.Subscriptions;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Subscriptions;

/// <summary>
/// <see cref="ISubscriberCredentials"/> by Keycloak standard token exchange: the caller's access token for a
/// refresh token tied to their session (ADR 0006 D10). It lives as long as they stay signed in; when it lapses
/// the notifier asks the subscriber to reauthorize.
/// </summary>
internal sealed class KeycloakSubscriberCredentials(IUserContext caller, IKeycloakTokenClient tokens) : ISubscriberCredentials
{
    public async Task<Result<string>> IssueAsync(CancellationToken cancellationToken)
    {
        var pair = await tokens.ExchangeForRefreshTokenAsync(caller.BearerToken, cancellationToken).ConfigureAwait(false);
        return pair.IsSuccess ? pair.Value.RefreshToken : pair.Error;
    }
}
