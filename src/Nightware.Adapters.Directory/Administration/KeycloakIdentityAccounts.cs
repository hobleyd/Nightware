using System.Text.Json.Nodes;

using Microsoft.Extensions.Options;

using Nightware.Adapters.Directory.Core;
using Nightware.Adapters.Directory.Core.Admin;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Administration;

/// <summary>
/// <see cref="IIdentityAccounts"/> over Keycloak users, as the console's own service account (ADR 0002 D5,
/// D9, D10). Profile edits are read-modify-write of the full user so nothing else is dropped. The
/// set-password e-mail names <see cref="DirectoryOptions.ClientId"/> (<c>nightware-admin</c> in the console).
/// </summary>
internal sealed class KeycloakIdentityAccounts(IKeycloakAdminClient keycloak, IOptions<DirectoryOptions> options) : IIdentityAccounts
{
    public async Task<Result<IdentityAccount>> GetAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return (await keycloak.GetUserAsync(userId, cancellationToken).ConfigureAwait(false)).Map(KeycloakAdministrationMapper.ToIdentity);
    }

    public async Task<Result<IdentityAccount>> FindByEmailAsync(string email, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        return (await keycloak.FindUserByEmailAsync(email, cancellationToken).ConfigureAwait(false)).Map(KeycloakAdministrationMapper.ToIdentity);
    }

    public async Task<Result<IdentityAccount>> FindByUsernameAsync(string username, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        return (await keycloak.FindUserByUsernameAsync(username, cancellationToken).ConfigureAwait(false)).Map(KeycloakAdministrationMapper.ToIdentity);
    }

    public async Task<Result<IdentityAccount>> CreateAsync(NewIdentity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        KeycloakNewUser user = new()
        {
            Username = identity.Username,
            Email = identity.Email.Value,
            FirstName = Blank(identity.FirstName),
            LastName = Blank(identity.LastName),
            Enabled = true,
            EmailVerified = true,
            RequiredActions = [.. identity.RequiredActions.Distinct().Select(KeycloakAdminNames.Alias)],
        };

        Result<string> id = await keycloak.CreateUserAsync(user, cancellationToken).ConfigureAwait(false);

        // Keycloak stores usernames lower-cased.
        return id.Map(created => new IdentityAccount(created, identity.Username.ToLowerInvariant(), identity.Email.Value, Enabled: true)
        {
            FirstName = user.FirstName,
            LastName = user.LastName,
        });
    }

    public async Task<Result> SetNameAsync(string userId, string? firstName, string? lastName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        // Null clears the name; Keycloak treats an empty string as "no value".
        string first = Blank(firstName) ?? string.Empty;
        string last = Blank(lastName) ?? string.Empty;
        Result<KeycloakUser> updated = await keycloak.UpdateUserAsync(
                userId,
                user =>
                {
                    bool changed = SetString(user, "firstName", first);
                    return SetString(user, "lastName", last) | changed;
                },
                cancellationToken)
            .ConfigureAwait(false);
        return updated.ToResult();
    }

    public async Task<Result> SetAliasesAsync(string userId, IReadOnlyList<MailAddress> aliases, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(aliases);
        IReadOnlyList<string>? values = aliases.Count == 0 ? null : [.. aliases.Select(static a => a.Value).Distinct(StringComparer.OrdinalIgnoreCase)];
        return await SetAttributeAsync(userId, KeycloakAdminNames.AliasesAttribute, values, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result> SetEnabledAsync(string userId, bool enabled, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        Result<KeycloakUser> updated = await keycloak.UpdateUserAsync(
                userId,
                user =>
                {
                    if (user["enabled"] is JsonValue current && current.TryGetValue(out bool value) && value == enabled)
                    {
                        return false;
                    }

                    user["enabled"] = enabled;
                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
        return updated.ToResult();
    }

    public Task<Result> EndSessionsAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return keycloak.LogoutUserAsync(userId, cancellationToken);
    }

    public Task<Result> SendActionEmailAsync(string userId, IReadOnlyList<RequiredAction> actions, TimeSpan lifespan, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(actions);
        if (actions.Count == 0)
        {
            return Task.FromResult<Result>(Error.Validation("Choose at least one action for the e-mail.", "actions"));
        }

        if (lifespan <= TimeSpan.Zero)
        {
            return Task.FromResult<Result>(Error.Validation("The link must stay valid for a positive time.", "lifespan"));
        }

        return keycloak.ExecuteActionsEmailAsync(
            userId,
            [.. actions.Distinct().Select(KeycloakAdminNames.Alias)],
            options.Value.ClientId,
            lifespan,
            cancellationToken);
    }

    public Task<Result> SetTemporaryPasswordAsync(string userId, TemporaryCredential credential, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(credential);
        return keycloak.ResetPasswordAsync(userId, credential.Password, temporary: true, cancellationToken);
    }

    public async Task<Result> AddRequiredActionAsync(string userId, RequiredAction action, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        string alias = KeycloakAdminNames.Alias(action);
        Result<KeycloakUser> updated = await keycloak.UpdateUserAsync(
                userId,
                user =>
                {
                    JsonArray actions = user["requiredActions"] as JsonArray ?? [];
                    if (actions.Any(a => a is JsonValue v && v.TryGetValue(out string? s) && string.Equals(s, alias, StringComparison.Ordinal)))
                    {
                        return false;
                    }

                    actions.Add(alias);
                    user["requiredActions"] = actions;
                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
        return updated.ToResult();
    }

    /// <summary>Users with the global-admin role mapped <b>directly</b> (the console only ever maps it directly).</summary>
    public async Task<Result<IReadOnlyList<IdentityAccount>>> ListGlobalAdminsAsync(CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<KeycloakUser>> users = await keycloak.ListRealmRoleUsersAsync(KeycloakAdminNames.GlobalAdminRole, cancellationToken).ConfigureAwait(false);
        return users.Map(KeycloakAdministrationMapper.ToIdentities);
    }

    /// <summary>Effective roles, so a role granted through a composite or a group counts too.</summary>
    public async Task<Result<bool>> IsGlobalAdminAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        Result<IReadOnlyList<KeycloakRole>> roles = await keycloak.GetEffectiveUserRealmRolesAsync(userId, cancellationToken).ConfigureAwait(false);
        return roles.Map(static r => HasGlobalAdmin(r));
    }

    /// <summary>
    /// Idempotent on the <b>direct</b> mapping: nothing is written when it already matches. Revoking does not
    /// remove the role from a composite or group that grants it (the console never sets those up).
    /// </summary>
    public async Task<Result> SetGlobalAdminAsync(string userId, bool granted, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        Result<IReadOnlyList<KeycloakRole>> direct = await keycloak.GetUserRealmRolesAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!direct.IsSuccess)
        {
            return direct.Error;
        }

        if (HasGlobalAdmin(direct.Value) == granted)
        {
            return Result.Success();
        }

        Result<KeycloakRole> role = await keycloak.GetRealmRoleAsync(KeycloakAdminNames.GlobalAdminRole, cancellationToken).ConfigureAwait(false);
        if (!role.IsSuccess)
        {
            return role.Error;
        }

        return granted
            ? await keycloak.AddUserRealmRolesAsync(userId, [role.Value], cancellationToken).ConfigureAwait(false)
            : await keycloak.RemoveUserRealmRolesAsync(userId, [role.Value], cancellationToken).ConfigureAwait(false);
    }

    public Task<Result> SetDeletionMarkerAsync(string userId, DeletionTarget target, DateTimeOffset? at, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        IReadOnlyList<string>? value = at is { } when ? [KeycloakAdministrationMapper.FormatMarker(when)] : null;
        return SetAttributeAsync(userId, KeycloakAdminNames.MarkerAttribute(target), value, cancellationToken);
    }

    internal static bool HasGlobalAdmin(IEnumerable<KeycloakRole> roles) =>
        roles.Any(static r => string.Equals(r.Name, KeycloakAdminNames.GlobalAdminRole, StringComparison.Ordinal));

    private async Task<Result> SetAttributeAsync(string userId, string name, IReadOnlyList<string>? values, CancellationToken cancellationToken)
    {
        Result<KeycloakUser> updated = await keycloak.UpdateUserAttributesAsync(
                userId,
                new Dictionary<string, IReadOnlyList<string>?> { [name] = values },
                cancellationToken)
            .ConfigureAwait(false);
        return updated.ToResult();
    }

    private static bool SetString(JsonObject user, string name, string value)
    {
        string current = user[name] is JsonValue v && v.TryGetValue(out string? s) ? s : string.Empty;
        if (string.Equals(current, value, StringComparison.Ordinal))
        {
            return false;
        }

        user[name] = value;
        return true;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
