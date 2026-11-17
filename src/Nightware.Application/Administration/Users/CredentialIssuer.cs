using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Users;

/// <summary>
/// Gets a user their first (or a replacement) password without an administrator ever choosing it
/// (ADR 0002 D9): Keycloak's own set-password e-mail, or a generated temporary password that Keycloak
/// forces the user to change at their next sign-in.
/// </summary>
public sealed class CredentialIssuer(IIdentityAccounts identities, IPasswordGenerator passwords, AdministrationOptions options)
{
    /// <returns>The temporary password to show once, or null when none was generated.</returns>
    public async Task<Result<TemporaryCredential?>> DeliverAsync(string userId, CredentialDelivery delivery, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        switch (delivery)
        {
            case CredentialDelivery.ActionEmail:
                Result sent = await identities.SendActionEmailAsync(userId, [RequiredAction.UpdatePassword], options.ActionEmailLifespan, cancellationToken).ConfigureAwait(false);
                return sent.IsSuccess ? Result<TemporaryCredential?>.Success(null) : sent.Error;

            case CredentialDelivery.TemporaryPassword:
                TemporaryCredential credential = passwords.Generate();
                Result set = await identities.SetTemporaryPasswordAsync(userId, credential, cancellationToken).ConfigureAwait(false);
                return set.IsSuccess ? credential : set.Error;

            default:
                return Result<TemporaryCredential?>.Success(null);
        }
    }
}
