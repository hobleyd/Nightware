using System.Text.Json.Nodes;

using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Core.Admin;

/// <summary>
/// Typed wrapper over the Keycloak admin REST API for the configured realm, authenticated as the
/// service account of <see cref="DirectoryOptions.ClientId"/>: <c>nightware-api</c> in the Graph host
/// (needs <c>realm-management</c> <c>view-users</c>/<c>query-users</c>, and <c>manage-users</c> for
/// attribute updates), <c>nightware-admin</c> in the admin console (additionally
/// <c>view-</c>/<c>query-</c>/<c>manage-organizations</c> for the organization calls, ADR 0002 D8).
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

    /// <summary>
    /// Read-modify-write of the full <c>UserRepresentation</c> (<c>GET</c>, then <c>PUT /users/{id}</c>
    /// when <paramref name="mutate"/> returns true). <paramref name="mutate"/> edits the raw JSON in place,
    /// so every field it does not touch is written back unchanged. Returns the user as written (or as read
    /// when nothing changed). Same lost-update caveat as <see cref="UpdateUserAttributesAsync"/>.
    /// </summary>
    Task<Result<KeycloakUser>> UpdateUserAsync(string id, Func<JsonObject, bool> mutate, CancellationToken cancellationToken);

    /// <summary><c>POST /users</c>; returns the new user's id (from <c>Location</c>). 409 → Conflict (username or email taken).</summary>
    Task<Result<string>> CreateUserAsync(KeycloakNewUser user, CancellationToken cancellationToken);

    /// <summary><c>POST /users/{id}/logout</c> — ends every session of the user.</summary>
    Task<Result> LogoutUserAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// <c>PUT /users/{id}/execute-actions-email?client_id=…&amp;lifespan=…</c> (lifespan in whole seconds)
    /// with the action aliases as a JSON array: Keycloak e-mails the user a link that performs them.
    /// </summary>
    Task<Result> ExecuteActionsEmailAsync(string id, IReadOnlyList<string> actions, string clientId, TimeSpan lifespan, CancellationToken cancellationToken);

    /// <summary>
    /// <c>PUT /users/{id}/reset-password</c> with <c>{type: "password", value, temporary}</c>. The password
    /// never appears in a log line or in the returned error's message.
    /// </summary>
    Task<Result> ResetPasswordAsync(string id, string password, bool temporary, CancellationToken cancellationToken);

    /// <summary><c>GET /users/{id}/credentials</c> — the user's credentials, in the order Keycloak ranks them at sign-in.</summary>
    Task<Result<IReadOnlyList<KeycloakCredential>>> ListUserCredentialsAsync(string userId, CancellationToken cancellationToken);

    /// <summary>
    /// <c>POST /users/{id}/credentials/{credentialId}/moveToFirst</c> — ranks the credential first, so a sign-in step
    /// that accepts several kinds offers this one by default.
    /// </summary>
    Task<Result> MoveCredentialToFirstAsync(string userId, string credentialId, CancellationToken cancellationToken);

    /// <summary><c>GET /roles/{name}</c> — a realm role; NotFound when the realm has none of that name.</summary>
    Task<Result<KeycloakRole>> GetRealmRoleAsync(string name, CancellationToken cancellationToken);

    /// <summary><c>GET /roles/{name}/users</c> (all pages, full representations) — users with the role mapped directly.</summary>
    Task<Result<IReadOnlyList<KeycloakUser>>> ListRealmRoleUsersAsync(string roleName, CancellationToken cancellationToken);

    /// <summary><c>GET /users/{id}/role-mappings/realm</c> — the realm roles mapped directly to the user.</summary>
    Task<Result<IReadOnlyList<KeycloakRole>>> GetUserRealmRolesAsync(string userId, CancellationToken cancellationToken);

    /// <summary><c>GET /users/{id}/role-mappings/realm/composite</c> — effective realm roles (composites and groups expanded).</summary>
    Task<Result<IReadOnlyList<KeycloakRole>>> GetEffectiveUserRealmRolesAsync(string userId, CancellationToken cancellationToken);

    /// <summary>
    /// The user's effective roles of the client <paramref name="clientId"/> (composites and groups expanded):
    /// <c>GET /clients?clientId=…</c>, then <c>GET /users/{id}/role-mappings/clients/{client}/composite</c>.
    /// Needs <c>query-clients</c>. NotFound when the realm has no such client.
    /// </summary>
    Task<Result<IReadOnlyList<KeycloakRole>>> GetEffectiveUserClientRolesAsync(string userId, string clientId, CancellationToken cancellationToken);

    /// <summary><c>POST /users/{id}/role-mappings/realm</c>.</summary>
    Task<Result> AddUserRealmRolesAsync(string userId, IReadOnlyList<KeycloakRole> roles, CancellationToken cancellationToken);

    /// <summary><c>DELETE /users/{id}/role-mappings/realm</c>.</summary>
    Task<Result> RemoveUserRealmRolesAsync(string userId, IReadOnlyList<KeycloakRole> roles, CancellationToken cancellationToken);

    /// <summary><c>GET /organizations</c> (all pages, brief representations — which include <c>domains</c>).</summary>
    Task<Result<IReadOnlyList<KeycloakOrganization>>> ListOrganizationsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// <c>GET /organizations?search=…&amp;exact=…</c> (all pages): organizations whose name or one of whose
    /// domains matches <paramref name="search"/> (exactly, when <paramref name="exact"/>).
    /// </summary>
    Task<Result<IReadOnlyList<KeycloakOrganization>>> SearchOrganizationsAsync(string search, bool exact, CancellationToken cancellationToken);

    /// <summary><c>GET /organizations/{id}</c>.</summary>
    Task<Result<KeycloakOrganization>> GetOrganizationAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// <c>POST /organizations</c>; returns the new organization's id (from <c>Location</c>). A taken name or
    /// alias is 409 → Conflict; a domain another organization holds is 400 → Validation.
    /// </summary>
    Task<Result<string>> CreateOrganizationAsync(KeycloakOrganization organization, CancellationToken cancellationToken);

    /// <summary>
    /// Read-modify-write of the full <c>OrganizationRepresentation</c> (<c>GET</c>, then <c>PUT
    /// /organizations/{id}</c> when <paramref name="mutate"/> returns true), preserving attributes, the alias
    /// and every field <paramref name="mutate"/> leaves alone. Returns the organization as written.
    /// </summary>
    Task<Result<KeycloakOrganization>> UpdateOrganizationAsync(string id, Func<JsonObject, bool> mutate, CancellationToken cancellationToken);

    /// <summary><c>GET /organizations/{id}/members</c> (all pages, full representations so attributes are included).</summary>
    Task<Result<IReadOnlyList<KeycloakUser>>> ListOrganizationMembersAsync(string organizationId, CancellationToken cancellationToken);

    /// <summary>
    /// <c>POST /organizations/{id}/members</c> with the user id as a JSON string: an unmanaged membership.
    /// Idempotent: Keycloak's 409 "already a member" is success.
    /// </summary>
    Task<Result> AddOrganizationMemberAsync(string organizationId, string userId, CancellationToken cancellationToken);

    /// <summary><c>GET /organizations/members/{userId}/organizations</c> — the organizations the user belongs to.</summary>
    Task<Result<IReadOnlyList<KeycloakOrganization>>> ListMemberOrganizationsAsync(string userId, CancellationToken cancellationToken);

    /// <summary>
    /// <c>GET /organizations/{id}/members/{userId}/groups</c> — the organization groups the member is in.
    /// NotFound when the user is not a member of the organization.
    /// </summary>
    Task<Result<IReadOnlyList<KeycloakGroup>>> ListMemberOrganizationGroupsAsync(string organizationId, string userId, CancellationToken cancellationToken);

    /// <summary>
    /// <c>GET /organizations/{id}/groups</c> without <c>search</c>/<c>q</c> (all pages): the organization's top-level
    /// groups. Their <c>parentId</c> is the organization's internal backing group, not null.
    /// </summary>
    Task<Result<IReadOnlyList<KeycloakGroup>>> ListOrganizationTopLevelGroupsAsync(string organizationId, CancellationToken cancellationToken);

    /// <summary><c>POST /organizations/{id}/groups</c> — a new top-level organization group; returns its id. 409 → Conflict (name taken).</summary>
    Task<Result<string>> CreateOrganizationGroupAsync(string organizationId, string name, CancellationToken cancellationToken);

    /// <summary>
    /// <c>GET /organizations/{id}/groups/{parentId}/children</c> (all pages): the parent's direct subgroups, without
    /// their attributes (Keycloak 26.8 leaves them out even with <c>briefRepresentation=false</c>).
    /// </summary>
    Task<Result<IReadOnlyList<KeycloakGroup>>> ListOrganizationGroupChildrenAsync(string organizationId, string parentId, CancellationToken cancellationToken);

    /// <summary><c>GET /organizations/{id}/groups/{groupId}</c>, with attributes. 404 → NotFound.</summary>
    Task<Result<KeycloakGroup>> GetOrganizationGroupAsync(string organizationId, string groupId, CancellationToken cancellationToken);

    /// <summary>
    /// <c>POST /organizations/{id}/groups/{parentId}/children</c> — a new subgroup; returns its id. Attributes sent
    /// here are ignored, so they are written with <see cref="UpdateOrganizationGroupAsync"/>. 409 → Conflict (name taken).
    /// </summary>
    Task<Result<string>> CreateOrganizationSubGroupAsync(string organizationId, string parentId, string name, CancellationToken cancellationToken);

    /// <summary>
    /// <c>PUT /organizations/{id}/groups/{groupId}</c> with the name and every attribute (a <c>PUT</c> without
    /// <c>attributes</c> keeps them; with them, replaces them). 404 → NotFound.
    /// </summary>
    Task<Result> UpdateOrganizationGroupAsync(string organizationId, string groupId, string name, IReadOnlyDictionary<string, IReadOnlyList<string>> attributes, CancellationToken cancellationToken);

    /// <summary><c>DELETE /organizations/{id}/groups/{groupId}</c>. 404 → NotFound.</summary>
    Task<Result> DeleteOrganizationGroupAsync(string organizationId, string groupId, CancellationToken cancellationToken);

    /// <summary><c>GET /organizations/{id}/groups/{groupId}/members</c> (all pages, full representations).</summary>
    Task<Result<IReadOnlyList<KeycloakUser>>> ListOrganizationGroupMembersAsync(string organizationId, string groupId, CancellationToken cancellationToken);

    /// <summary>
    /// <c>PUT /organizations/{id}/groups/{groupId}/members/{userId}</c>. Idempotent (409 "already a member of
    /// the group" is success); 400 → Validation when the user is not a member of the organization.
    /// </summary>
    Task<Result> AddOrganizationGroupMemberAsync(string organizationId, string groupId, string userId, CancellationToken cancellationToken);

    /// <summary>
    /// <c>DELETE /organizations/{id}/groups/{groupId}/members/{userId}</c>. Keycloak answers 400 when the user
    /// is not in the group, so callers check membership first.
    /// </summary>
    Task<Result> RemoveOrganizationGroupMemberAsync(string organizationId, string groupId, string userId, CancellationToken cancellationToken);

    /// <summary><c>GET /organizations/{id}/groups/{groupId}/role-mappings/realm</c>.</summary>
    Task<Result<IReadOnlyList<KeycloakRole>>> GetOrganizationGroupRealmRolesAsync(string organizationId, string groupId, CancellationToken cancellationToken);

    /// <summary><c>POST /organizations/{id}/groups/{groupId}/role-mappings/realm</c>.</summary>
    Task<Result> AddOrganizationGroupRealmRolesAsync(string organizationId, string groupId, IReadOnlyList<KeycloakRole> roles, CancellationToken cancellationToken);
}
