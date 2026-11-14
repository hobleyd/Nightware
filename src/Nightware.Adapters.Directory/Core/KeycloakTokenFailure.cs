namespace Nightware.Adapters.Directory.Core;

/// <summary>Why Keycloak's token endpoint did not issue a token, from the façade's point of view.</summary>
internal enum KeycloakTokenFailure
{
    /// <summary>The caller's (subject) token is invalid, expired or its session was revoked — the client must sign in again.</summary>
    SubjectRejected,

    /// <summary>
    /// Keycloak rejected the façade itself (bad client secret, exchange not enabled, audience not
    /// available to <c>nightware-api</c>, …). Re-authenticating the user cannot fix this.
    /// </summary>
    Misconfigured,

    /// <summary>Keycloak is unreachable, timed out, throttled us or failed (5xx).</summary>
    Unavailable,
}
