using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Core.Admin;

/// <summary>
/// Typed wrapper over the Keycloak admin REST API for the configured realm, authenticated as the
/// <c>nightware-api</c> service account (needs <c>realm-management</c> <c>view-users</c>/<c>query-users</c>,
/// and <c>manage-users</c> for attribute updates).
/// </summary>
/// <remarks>
/// Error mapping: 400 → Validation, 401 (after one token refresh) → BackendUnavailable (the façade's
/// credentials, not the caller's), 403 → Forbidden, 404 → NotFound, 409 → Conflict, 429 → Throttled,
/// 5xx/unreachable/timeout → BackendUnavailable. GETs are retried on transient failures; writes are not.
/// </remarks>
public interface IKeycloakAdminClient
{
    /// <summary><c>GET /users/{id}</c>.</summary>
    Task<Result<KeycloakUser>> GetUserAsync(string id, CancellationToken cancellationToken);

    /// <summary><c>GET /users?…</c> — search, exact lookups and first/max paging.</summary>
    Task<Result<IReadOnlyList<KeycloakUser>>> FindUsersAsync(KeycloakUserQuery query, CancellationToken cancellationToken);

    /// <summary><c>GET /users/count?…</c> — paging fields of <paramref name="query"/> are ignored.</summary>
    Task<Result<int>> CountUsersAsync(KeycloakUserQuery query, CancellationToken cancellationToken);

    /// <summary>The single user with exactly this email; NotFound when none matches.</summary>
    Task<Result<KeycloakUser>> FindUserByEmailAsync(string email, CancellationToken cancellationToken);

    /// <summary>The single user with exactly this username; NotFound when none matches.</summary>
    Task<Result<KeycloakUser>> FindUserByUsernameAsync(string username, CancellationToken cancellationToken);

    /// <summary>The user's custom attributes (empty when they have none).</summary>
    Task<Result<IReadOnlyDictionary<string, IReadOnlyList<string>>>> GetUserAttributesAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Merges <paramref name="changes"/> into the user's attributes: a list replaces that attribute,
    /// null removes it, unnamed attributes are kept. Read-modify-write (<c>GET</c> then full
    /// <c>PUT /users/{id}</c>) because a partial PUT can drop attributes; Keycloak has no ETag, so a
    /// concurrent update of the same user may be lost. Returns the user as written.
    /// </summary>
    /// <remarks>
    /// With Keycloak's declarative user profile (24+), attributes the realm's user profile does not
    /// declare are silently dropped unless the realm's <c>unmanagedAttributePolicy</c> allows them.
    /// </remarks>
    Task<Result<KeycloakUser>> UpdateUserAttributesAsync(
        string id,
        IReadOnlyDictionary<string, IReadOnlyList<string>?> changes,
        CancellationToken cancellationToken);
}
