using System.Net;

namespace Nightware.Adapters.Directory.Core;

/// <summary>
/// Classifies a non-2xx token-endpoint response. Keycloak's RFC 8693 errors are not documented
/// as a table (https://www.keycloak.org/securing-apps/token-exchange, "Token exchange - Additional
/// details"); the cases below were observed against Keycloak 26.8 (deploy/ stack):
/// <list type="bullet">
/// <item>garbage, tampered, expired or logged-out subject token → <c>400 invalid_request "Invalid token"</c></item>
/// <item>audience that is a client but not available to the requester → <c>400 invalid_request "Requested audience not available: x"</c></item>
/// <item>audience that is not a client → <c>400 invalid_client "Audience not found"</c></item>
/// <item>wrong client secret → <c>401 unauthorized_client "Invalid client or Invalid client credentials"</c></item>
/// <item>requester not in the subject token's <c>aud</c> → <c>403 access_denied "Client is not within the token audience"</c> (from the provider source)</item>
/// </list>
/// RFC 6749 §5.2 <c>invalid_grant</c> and RFC 6750 <c>invalid_token</c> are treated as subject rejections too.
/// </summary>
internal static class KeycloakTokenErrorClassifier
{
    public static KeycloakTokenFailure ClassifyExchange(HttpStatusCode status, OAuthErrorResponse? error)
    {
        if (status == HttpStatusCode.TooManyRequests || (int)status >= 500)
        {
            return KeycloakTokenFailure.Unavailable;
        }

        string code = error?.Error ?? string.Empty;
        return code switch
        {
            "invalid_grant" or "invalid_token" or "access_denied" => KeycloakTokenFailure.SubjectRejected,
            "invalid_request" when IsInvalidSubjectToken(error?.ErrorDescription) => KeycloakTokenFailure.SubjectRejected,
            _ when status == HttpStatusCode.Forbidden && code.Length == 0 => KeycloakTokenFailure.SubjectRejected,
            _ => KeycloakTokenFailure.Misconfigured,
        };
    }

    /// <summary>A client-credentials grant only involves the façade's own credentials, so no failure is the caller's fault.</summary>
    public static KeycloakTokenFailure ClassifyClientCredentials(HttpStatusCode status) =>
        status == HttpStatusCode.TooManyRequests || (int)status >= 500
            ? KeycloakTokenFailure.Unavailable
            : KeycloakTokenFailure.Misconfigured;

    private static bool IsInvalidSubjectToken(string? description) =>
        description is not null
        && (description.Equals("Invalid token", StringComparison.OrdinalIgnoreCase)
            || description.Contains("token expired", StringComparison.OrdinalIgnoreCase)
            || description.Contains("token is not active", StringComparison.OrdinalIgnoreCase));
}
