using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Options;

using Nightware.Application.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Administration;

/// <summary>
/// Nextcloud users through the provisioning API (<c>/ocs/v1.php/cloud/users</c>) and <c>user_oidc</c>'s own
/// pre-provisioning endpoint (<c>POST /ocs/v2.php/apps/user_oidc/api/v1/user</c>, ADR 0002 D5).
/// </summary>
/// <remarks>
/// OCS codes per operation, from Nextcloud's <c>provisioning_api</c> <c>UsersController</c>:
/// <c>GET users/{uid}</c> answers 404 (<c>OCSNotFoundException</c>) or 998 for a missing user;
/// <c>PUT …/enable|disable</c> answers <b>101</b> for a missing user (or the caller itself);
/// <c>DELETE users/{uid}</c> answers 998 for a missing user and 101 for itself or a failed delete.
/// v1 carries all of these in an HTTP 200.
/// </remarks>
internal sealed class NextcloudGroupwareAccounts(NextcloudProvisioningClient client, IOptions<NextcloudProvisioningOptions> options) : IGroupwareAccounts
{
    private const string UsersPath = "ocs/v1.php/cloud/users/";
    private const string UserOidcUserPath = "ocs/v2.php/apps/user_oidc/api/v1/user";
    private const string UserOidcProviderPath = "ocs/v2.php/apps/user_oidc/api/v1/provider";

    public async Task<Result<GroupwareAccount>> FindAsync(string uid, CancellationToken cancellationToken)
    {
        if (InvalidUid(uid) is { } invalid)
        {
            return invalid;
        }

        var sent = await client.SendAsync(HttpMethod.Get, UsersPath + Escape(uid), null, cancellationToken).ConfigureAwait(false);
        if (!sent.IsSuccess)
        {
            return sent.Error;
        }

        var reply = sent.Value;
        if (!reply.Succeeded)
        {
            return reply.ToError($"No Nextcloud user {uid}.");
        }

        if (reply.Data is not JsonObject data || Text(data, "id") is not { } id)
        {
            return Error.BackendUnavailable("Nextcloud sent an unreadable user record.");
        }

        var backend = Text(data, "backend");
        var managed = backend is not null && options.Value.EffectiveIdentityProviderBackends.Contains(backend, StringComparer.OrdinalIgnoreCase);
        return new GroupwareAccount(
            id,
            Text(data, "email"),
            Text(data, "displayname") ?? Text(data, "display-name"),
            Bool(data, "enabled") ?? true,
            managed);
    }

    public async Task<Result> PreProvisionAsync(string uid, string email, string displayName, CancellationToken cancellationToken)
    {
        if (InvalidUid(uid) is { } invalid)
        {
            return invalid;
        }

        var provider = await ResolveProviderIdAsync(cancellationToken).ConfigureAwait(false);
        if (!provider.IsSuccess)
        {
            return provider.Error;
        }

        var body = new JsonObject { ["providerId"] = provider.Value, ["userId"] = uid };
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            body["displayName"] = displayName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            body["email"] = email.Trim();
        }

        var sent = await client.SendAsync(HttpMethod.Post, UserOidcUserPath, body, cancellationToken).ConfigureAwait(false);
        if (!sent.IsSuccess)
        {
            return sent.Error;
        }

        var reply = sent.Value;
        if (reply.Succeeded)
        {
            // user_oidc creates or updates (idempotent) and answers the uid it used; with unique uids on
            // (--unique-uid=1, the user_oidc default) that is a hash, which first sign-in would also produce
            // but which is not the uid the console links by.
            var created = reply.Data is JsonObject data ? Text(data, "user_id") : null;
            return created is null || string.Equals(created, uid, StringComparison.Ordinal)
                ? Result.Success()
                : Error.Conflict($"Nextcloud's user_oidc created the user as '{created}', not '{uid}'; the provider must use --unique-uid=0 with the username as uid.");
        }

        if (reply.IsOcsNotFound)
        {
            return Unavailable();
        }

        return reply.StatusCode == 409 && reply.IsEnvelope
            ? Error.Conflict($"Nextcloud user {uid} exists and cannot be taken over by user_oidc.")
            : reply.ToError($"No Nextcloud user {uid}.");
    }

    public async Task<Result> SetEnabledAsync(string uid, bool enabled, CancellationToken cancellationToken)
    {
        if (InvalidUid(uid) is { } invalid)
        {
            return invalid;
        }

        var sent = await client.SendAsync(HttpMethod.Put, UsersPath + Escape(uid) + (enabled ? "/enable" : "/disable"), null, cancellationToken).ConfigureAwait(false);
        if (!sent.IsSuccess)
        {
            return sent.Error;
        }

        var reply = sent.Value;
        if (reply.Succeeded)
        {
            return Result.Success();
        }

        return reply.IsEnvelope && reply.StatusCode == 101
            ? Error.NotFound($"No Nextcloud user {uid} (or it is the admin console's own account).")
            : reply.ToError($"No Nextcloud user {uid}.");
    }

    public async Task<Result> DeleteAsync(string uid, CancellationToken cancellationToken)
    {
        if (InvalidUid(uid) is { } invalid)
        {
            return invalid;
        }

        var sent = await client.SendAsync(HttpMethod.Delete, UsersPath + Escape(uid), null, cancellationToken).ConfigureAwait(false);
        if (!sent.IsSuccess)
        {
            return sent.Error;
        }

        var reply = sent.Value;
        if (reply.Succeeded)
        {
            return Result.Success();
        }

        return reply.IsEnvelope && reply.StatusCode == 101
            ? Error.BackendUnavailable($"Nextcloud did not delete {uid} (it is the admin console's own account, or the deletion failed).")
            : reply.ToError($"No Nextcloud user {uid}.");
    }

    /// <summary>The configured numeric id, or the id of the provider whose identifier is configured.</summary>
    private async Task<Result<int>> ResolveProviderIdAsync(CancellationToken cancellationToken)
    {
        var configured = options.Value;
        if (configured.NumericOidcProviderId is { } id)
        {
            return id;
        }

        var identifier = configured.OidcProviderId?.Trim() ?? string.Empty;
        var sent = await client.SendAsync(HttpMethod.Get, UserOidcProviderPath, null, cancellationToken).ConfigureAwait(false);
        if (!sent.IsSuccess)
        {
            return sent.Error;
        }

        var reply = sent.Value;
        if (!reply.Succeeded)
        {
            return reply.IsOcsNotFound ? Unavailable() : reply.ToError("No user_oidc providers.");
        }

        if (reply.Data is not JsonArray providers)
        {
            return Error.BackendUnavailable("Nextcloud sent an unreadable user_oidc provider list.");
        }

        foreach (var provider in providers.OfType<JsonObject>())
        {
            if (string.Equals(Text(provider, "identifier"), identifier, StringComparison.Ordinal)
                && provider["id"] is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue(out int found))
            {
                return found;
            }
        }

        return Error.BackendUnavailable($"Nextcloud has no user_oidc provider '{identifier}'; check NextcloudProvisioning:OidcProviderId.");
    }

    private static Error Unavailable() =>
        Error.Unsupported("Nextcloud's user_oidc provisioning API is not available; the user is created at first sign-in.");

    private static Error? InvalidUid(string uid) =>
        string.IsNullOrWhiteSpace(uid) || uid.Trim('.').Length == 0 || uid.Contains('/', StringComparison.Ordinal)
            ? Error.Validation("Not a Nextcloud user id.", "uid")
            : null;

    private static string Escape(string uid) => Uri.EscapeDataString(uid);

    private static string? Text(JsonObject data, string name) =>
        data[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    private static bool? Bool(JsonObject data, string name) =>
        data[name] is JsonValue value
            ? value.GetValueKind() switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;
}
