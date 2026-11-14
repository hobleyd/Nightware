using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Core;

/// <summary>Talks to Keycloak's token endpoint as the confidential <c>nightware-api</c> client.</summary>
public interface IKeycloakTokenClient
{
    /// <summary>
    /// A client-credentials token for the client's service account, cached until shortly before it
    /// expires. Concurrent callers share one in-flight refresh. Failures are
    /// <see cref="ErrorKind.BackendUnavailable"/>: they are the façade's problem, never the caller's.
    /// </summary>
    Task<Result<AccessToken>> GetServiceAccountTokenAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Drops the cached service-account token if it is still <paramref name="rejected"/> (e.g. the
    /// admin API answered 401 because the token was revoked or Keycloak restarted).
    /// </summary>
    void InvalidateServiceAccountToken(AccessToken rejected);

    /// <summary>
    /// RFC 8693 standard token exchange: trades the caller's access token for one whose <c>aud</c> is
    /// <paramref name="audience"/>. Not cached here and never retried. A rejected subject token is
    /// <see cref="ErrorKind.BackendAuthFailed"/>; anything else is <see cref="ErrorKind.BackendUnavailable"/>.
    /// </summary>
    Task<Result<AccessToken>> ExchangeAsync(string subjectToken, string audience, CancellationToken cancellationToken);
}
