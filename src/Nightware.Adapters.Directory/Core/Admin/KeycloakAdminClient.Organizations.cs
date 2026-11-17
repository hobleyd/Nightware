using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Core.Admin;

/// <summary>
/// Organization calls (Keycloak 26 "Organizations" admin REST resource; organization groups since 26.6,
/// verified against the 26.8.2 sources of <c>OrganizationsResource</c>, <c>OrganizationMemberResource</c>,
/// <c>OrganizationGroupsResource</c> and <c>OrganizationGroupResource</c>).
/// </summary>
internal sealed partial class KeycloakAdminClient
{
    private const string OrganizationNotFound = "The entity was not found in the directory.";

    public Task<Result<IReadOnlyList<KeycloakOrganization>>> ListOrganizationsAsync(CancellationToken cancellationToken) =>
        ListAllAsync<KeycloakOrganization>(static paging => "organizations?briefRepresentation=true&" + paging, cancellationToken);

    public Task<Result<IReadOnlyList<KeycloakOrganization>>> SearchOrganizationsAsync(string search, bool exact, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(search);
        string query = $"organizations?briefRepresentation=true&search={Uri.EscapeDataString(search)}&exact={(exact ? "true" : "false")}&";
        return ListAllAsync<KeycloakOrganization>(paging => query + paging, cancellationToken);
    }

    public Task<Result<KeycloakOrganization>> GetOrganizationAsync(string id, CancellationToken cancellationToken)
    {
        string path = OrganizationPath(id);
        return SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, Url(path)),
            ReadJsonAsync<KeycloakOrganization>,
            cancellationToken,
            mapError: NotFoundAs(OrganizationNotFound));
    }

    public Task<Result<string>> CreateOrganizationAsync(KeycloakOrganization organization, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organization);
        return SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, Url("organizations")) { Content = JsonBody(organization) },
            ReadCreatedIdAsync,
            cancellationToken);
    }

    public async Task<Result<KeycloakOrganization>> UpdateOrganizationAsync(string id, Func<JsonObject, bool> mutate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        string path = OrganizationPath(id);
        Result<JsonObject> current = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Get, Url(path)),
                ReadJsonAsync<JsonObject>,
                cancellationToken,
                mapError: NotFoundAs(OrganizationNotFound))
            .ConfigureAwait(false);
        if (!current.IsSuccess)
        {
            return current.Error;
        }

        JsonObject representation = current.Value;
        if (mutate(representation))
        {
            string body = representation.ToJsonString();
            Result written = await WriteAsync(HttpMethod.Put, path, () => JsonBody(body), cancellationToken, mapError: NotFoundAs(OrganizationNotFound)).ConfigureAwait(false);
            if (!written.IsSuccess)
            {
                return written.Error;
            }
        }

        return representation.Deserialize<KeycloakOrganization>(Json)!;
    }

    public Task<Result<IReadOnlyList<KeycloakUser>>> ListOrganizationMembersAsync(string organizationId, CancellationToken cancellationToken)
    {
        string path = OrganizationPath(organizationId) + "/members?briefRepresentation=false&";
        return ListAllAsync<KeycloakUser>(paging => path + paging, cancellationToken);
    }

    public Task<Result> AddOrganizationMemberAsync(string organizationId, string userId, CancellationToken cancellationToken)
    {
        // The body is the bare user id as a JSON string (Keycloak trims surrounding quotes, issue 34401).
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        string body = JsonSerializer.Serialize(userId);
        return WriteAsync(
            HttpMethod.Post,
            OrganizationPath(organizationId) + "/members",
            () => JsonBody(body),
            cancellationToken,
            alsoSuccess: HttpStatusCode.Conflict);
    }

    public Task<Result<IReadOnlyList<KeycloakOrganization>>> ListMemberOrganizationsAsync(string userId, CancellationToken cancellationToken) =>
        GetListAsync<KeycloakOrganization>($"organizations/members/{Segment(userId)}/organizations?briefRepresentation=true", cancellationToken);

    public Task<Result<IReadOnlyList<KeycloakGroup>>> ListMemberOrganizationGroupsAsync(string organizationId, string userId, CancellationToken cancellationToken) =>
        GetListAsync<KeycloakGroup>(
            $"{OrganizationPath(organizationId)}/members/{Segment(userId)}/groups?briefRepresentation=true",
            cancellationToken,
            NotFoundAs("The user is not a member of the entity."));

    public async Task<Result<IReadOnlyList<KeycloakGroup>>> ListOrganizationTopLevelGroupsAsync(string organizationId, CancellationToken cancellationToken)
    {
        // Without search/q Keycloak returns the top-level groups only (OrganizationGroupsResource.getGroups).
        string path = OrganizationPath(organizationId) + "/groups?briefRepresentation=true&";
        Result<IReadOnlyList<KeycloakGroup>> groups = await ListAllAsync<KeycloakGroup>(paging => path + paging, cancellationToken).ConfigureAwait(false);
        return groups.IsSuccess ? groups : NotFoundAs(OrganizationNotFound)(groups.Error);
    }

    public Task<Result<string>> CreateOrganizationGroupAsync(string organizationId, string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string path = OrganizationPath(organizationId) + "/groups";
        string body = new JsonObject { ["name"] = name }.ToJsonString();
        return SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, Url(path)) { Content = JsonBody(body) },
            ReadCreatedIdAsync,
            cancellationToken,
            mapError: NotFoundAs(OrganizationNotFound));
    }

    public Task<Result<IReadOnlyList<KeycloakUser>>> ListOrganizationGroupMembersAsync(string organizationId, string groupId, CancellationToken cancellationToken)
    {
        string path = $"{OrganizationPath(organizationId)}/groups/{Segment(groupId)}/members?briefRepresentation=false&";
        return ListAllAsync<KeycloakUser>(paging => path + paging, cancellationToken);
    }

    public Task<Result> AddOrganizationGroupMemberAsync(string organizationId, string groupId, string userId, CancellationToken cancellationToken) =>
        WriteAsync(
            HttpMethod.Put,
            $"{OrganizationPath(organizationId)}/groups/{Segment(groupId)}/members/{Segment(userId)}",
            static () => null,
            cancellationToken,
            alsoSuccess: HttpStatusCode.Conflict);

    public Task<Result> RemoveOrganizationGroupMemberAsync(string organizationId, string groupId, string userId, CancellationToken cancellationToken) =>
        WriteAsync(HttpMethod.Delete, $"{OrganizationPath(organizationId)}/groups/{Segment(groupId)}/members/{Segment(userId)}", static () => null, cancellationToken);

    public Task<Result<IReadOnlyList<KeycloakRole>>> GetOrganizationGroupRealmRolesAsync(string organizationId, string groupId, CancellationToken cancellationToken) =>
        GetListAsync<KeycloakRole>($"{OrganizationPath(organizationId)}/groups/{Segment(groupId)}/role-mappings/realm", cancellationToken);

    public Task<Result> AddOrganizationGroupRealmRolesAsync(string organizationId, string groupId, IReadOnlyList<KeycloakRole> roles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return WriteAsync(HttpMethod.Post, $"{OrganizationPath(organizationId)}/groups/{Segment(groupId)}/role-mappings/realm", () => JsonBody(roles), cancellationToken);
    }

    private static string OrganizationPath(string id) => "organizations/" + Segment(id);
}
