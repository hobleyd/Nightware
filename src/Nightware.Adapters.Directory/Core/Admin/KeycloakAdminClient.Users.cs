using System.Globalization;
using System.Text.Json.Nodes;

using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Core.Admin;

/// <summary>User lifecycle and realm-role calls (ADR 0002 D5, D9, D10).</summary>
internal sealed partial class KeycloakAdminClient
{
    public Task<Result<string>> CreateUserAsync(KeycloakNewUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        return SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, Url("users")) { Content = JsonBody(user) },
            ReadCreatedIdAsync,
            cancellationToken);
    }

    public Task<Result> LogoutUserAsync(string id, CancellationToken cancellationToken) =>
        WriteAsync(HttpMethod.Post, $"users/{Segment(id)}/logout", static () => null, cancellationToken);

    public Task<Result> ExecuteActionsEmailAsync(string id, IReadOnlyList<string> actions, string clientId, TimeSpan lifespan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifespan, TimeSpan.Zero);
        string seconds = ((long)Math.Ceiling(lifespan.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        string body = new JsonArray([.. actions.Select(static a => JsonValue.Create(a))]).ToJsonString();
        return WriteAsync(
            HttpMethod.Put,
            $"users/{Segment(id)}/execute-actions-email?client_id={Uri.EscapeDataString(clientId)}&lifespan={seconds}",
            () => JsonBody(body),
            cancellationToken);
    }

    public Task<Result> ResetPasswordAsync(string id, string password, bool temporary, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        JsonObject credential = new()
        {
            ["type"] = "password",
            ["value"] = password,
            ["temporary"] = temporary,
        };

        // Keycloak's 400 text comes from the password policy; never let a server echo of the value reach the
        // error message (which pages render and use cases may log): replace it with a fixed one.
        return WriteAsync(
            HttpMethod.Put,
            $"users/{Segment(id)}/reset-password",
            () => JsonBody(credential.ToJsonString()),
            cancellationToken,
            mapError: static error => error.Kind switch
            {
                ErrorKind.Validation => Error.Validation("The directory's password policy rejected the generated password."),
                ErrorKind.Conflict => Error.Conflict("The directory refused to set the password."),
                ErrorKind.NotFound => Error.NotFound("The user was not found in the directory."),
                _ => error,
            });
    }

    public Task<Result<KeycloakRole>> GetRealmRoleAsync(string name, CancellationToken cancellationToken)
    {
        string path = $"roles/{Segment(name)}";
        return SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, Url(path)),
            ReadJsonAsync<KeycloakRole>,
            cancellationToken,
            mapError: NotFoundAs($"The realm role '{name}' does not exist in the directory."));
    }

    public Task<Result<IReadOnlyList<KeycloakUser>>> ListRealmRoleUsersAsync(string roleName, CancellationToken cancellationToken)
    {
        string path = $"roles/{Segment(roleName)}/users?briefRepresentation=false&";
        return ListAllAsync<KeycloakUser>(paging => path + paging, cancellationToken);
    }

    public Task<Result<IReadOnlyList<KeycloakRole>>> GetUserRealmRolesAsync(string userId, CancellationToken cancellationToken) =>
        GetListAsync<KeycloakRole>($"users/{Segment(userId)}/role-mappings/realm", cancellationToken);

    public Task<Result<IReadOnlyList<KeycloakRole>>> GetEffectiveUserRealmRolesAsync(string userId, CancellationToken cancellationToken) =>
        GetListAsync<KeycloakRole>($"users/{Segment(userId)}/role-mappings/realm/composite", cancellationToken);

    public async Task<Result<IReadOnlyList<KeycloakRole>>> GetEffectiveUserClientRolesAsync(string userId, string clientId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        Result<IReadOnlyList<KeycloakClientRef>> clients = await GetListAsync<KeycloakClientRef>($"clients?clientId={Uri.EscapeDataString(clientId)}", cancellationToken).ConfigureAwait(false);
        if (!clients.IsSuccess)
        {
            return clients.Error;
        }

        string? clientUuid = clients.Value.FirstOrDefault(c => string.Equals(c.ClientId, clientId, StringComparison.Ordinal))?.Id;
        if (clientUuid is null)
        {
            // query-clients alone lists no clients, and view-clients would expose every client's secret. Our own
            // service account holds roles of the client we ask about (realm-management), so its role mappings,
            // readable with view-users, name the client's id.
            Result<string?> own = await ClientUuidFromOwnServiceAccountAsync(clientId, cancellationToken).ConfigureAwait(false);
            if (!own.IsSuccess)
            {
                return own.Error;
            }

            clientUuid = own.Value;
        }

        return clientUuid is null
            ? Error.NotFound($"The realm has no client {clientId}.")
            : await GetListAsync<KeycloakRole>($"users/{Segment(userId)}/role-mappings/clients/{Segment(clientUuid)}/composite", cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<string?>> ClientUuidFromOwnServiceAccountAsync(string clientId, CancellationToken cancellationToken)
    {
        Result<KeycloakUser> self = await FindUserByUsernameAsync("service-account-" + options.Value.ClientId.ToLowerInvariant(), cancellationToken).ConfigureAwait(false);
        if (!self.IsSuccess)
        {
            return self.Error.Kind == ErrorKind.NotFound ? Result<string?>.Success(null) : self.Error;
        }

        Result<JsonObject> mappings = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Get, Url($"users/{Segment(self.Value.Id)}/role-mappings")),
                ReadJsonAsync<JsonObject>,
                cancellationToken)
            .ConfigureAwait(false);
        return mappings.Map(m => m["clientMappings"]?[clientId]?["id"]?.GetValue<string>());
    }

    public Task<Result> AddUserRealmRolesAsync(string userId, IReadOnlyList<KeycloakRole> roles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return WriteAsync(HttpMethod.Post, $"users/{Segment(userId)}/role-mappings/realm", () => JsonBody(roles), cancellationToken);
    }

    public Task<Result> RemoveUserRealmRolesAsync(string userId, IReadOnlyList<KeycloakRole> roles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return WriteAsync(HttpMethod.Delete, $"users/{Segment(userId)}/role-mappings/realm", () => JsonBody(roles), cancellationToken);
    }
}
