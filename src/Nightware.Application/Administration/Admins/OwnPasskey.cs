using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Admins;

/// <summary>
/// After the signed-in administrator adds a passkey, makes it what Keycloak asks for at their next sign-in;
/// otherwise the authenticator code they set up first stays the default. Only ever their own credentials.
/// </summary>
public sealed class PreferOwnPasskey(IAdminActor actor, IIdentityAccounts identities)
{
    public async Task<Result> ExecuteAsync(CancellationToken cancellationToken)
    {
        Result allowed = AdminAuthorization.RequireAdmin(actor.Current);
        return allowed.IsSuccess ? await identities.PreferNewestPasskeyAsync(actor.Current.UserId, cancellationToken).ConfigureAwait(false) : allowed;
    }
}
