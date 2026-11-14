using Nightware.Application.Common.Identity;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Directory;

/// <summary>
/// <c>GET /me</c>: the caller's directory profile. The token's <c>sub</c> is the directory id; the
/// claims fill what the directory lacks.
/// </summary>
/// <remarks>
/// A caller the directory does not know (e.g. a token from a federated identity provider that was never
/// imported) still gets a profile built from the token, because account setup and sign-in verification
/// need <c>mail</c>. Every other failure propagates: masking an outage with a thinner profile would let
/// the client cache wrong signature details.
/// </remarks>
public sealed class GetSignedInUser(IUserContext user, IUserDirectory directory)
{
    public async Task<Result<DirectoryUser>> ExecuteAsync(CancellationToken cancellationToken)
    {
        var principal = user.Principal;
        var found = await directory.GetAsync(principal.Id, cancellationToken).ConfigureAwait(false);
        if (found.IsSuccess)
        {
            var profile = found.Value;
            return string.IsNullOrWhiteSpace(profile.Mail) && !string.IsNullOrWhiteSpace(principal.Mail)
                ? profile with { Mail = principal.Mail }
                : profile;
        }

        return found.Error.Kind == ErrorKind.NotFound ? FromClaims(principal) : found;
    }

    private static DirectoryUser FromClaims(Principal principal)
    {
        var mail = string.IsNullOrWhiteSpace(principal.Mail) ? null : principal.Mail;

        // Graph's UPN is always an address; a bare login name ('alice') would be read as one by the client.
        var upn = DirectoryUserLookup.IsAddress(principal.Upn) ? principal.Upn : mail ?? principal.Upn;
        return new DirectoryUser(principal.Id, principal.DisplayName, upn) { Mail = mail };
    }
}
