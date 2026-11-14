using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Core;

/// <summary>
/// Which credentials the Groupware backend accepts. Nextcloud has no DAV impersonation
/// (nothing like Stalwart's master user): a service-account credential would act as the
/// service account itself and read the wrong user's calendars. Only per-user credentials
/// (pass-through or RFC 8693 token exchange, audience <c>nextcloud</c>) are accepted.
/// </summary>
public static class DavCredentialPolicy
{
    public static Result<BackendCredential> Check(BackendCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (credential.Strategy == CredentialStrategy.ServiceAccount || credential.ActAs is not null)
        {
            return Error.Unsupported(
                "The Groupware backend (Nextcloud) cannot be reached with a service account: Nextcloud has no DAV impersonation. Configure token exchange or pass-through for Groupware.");
        }

        return credential;
    }
}
