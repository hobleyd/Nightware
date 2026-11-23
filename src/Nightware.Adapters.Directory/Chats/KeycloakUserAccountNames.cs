using Microsoft.Extensions.Caching.Memory;

using Nightware.Adapters.Directory.Core.Admin;
using Nightware.Adapters.Directory.Directory;
using Nightware.Application.Directory;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Chats;

/// <summary>
/// <see cref="IUserAccountNames"/> over the Keycloak admin API (service account): Nextcloud's uid is the Keycloak
/// username (<c>user_oidc --mapping-uid=preferred_username</c>). A chat page names the same few people again and
/// again, so answers are memoised for <see cref="Lifetime"/> in the disposable memory cache; a miss is not.
/// </summary>
internal sealed class KeycloakUserAccountNames(IKeycloakAdminClient admin, IUserDirectory directory, IMemoryCache cache) : IUserAccountNames
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public async Task<Result<string>> AccountNameOfAsync(string user, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        var key = ("nightware.account-name", user.Trim().ToLowerInvariant());
        if (cache.TryGetValue(key, out string? known) && known is not null)
        {
            return known;
        }

        var found = await directory.GetAsync(user, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            return found.Error;
        }

        // The directory user carries no username; read it by id (one more request, then cached).
        var keycloak = await admin.GetUserAsync(found.Value.Id, cancellationToken).ConfigureAwait(false);
        if (!keycloak.IsSuccess)
        {
            return keycloak.Error;
        }

        if (string.IsNullOrEmpty(keycloak.Value.Username))
        {
            return Error.NotFound("The user has no account name.");
        }

        cache.Set(key, keycloak.Value.Username, Lifetime);
        cache.Set(("nightware.account-user", keycloak.Value.Username), found.Value, Lifetime);
        return keycloak.Value.Username;
    }

    public async Task<Result<DirectoryUser>> UserOfAccountAsync(string accountName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        var key = ("nightware.account-user", accountName);
        if (cache.TryGetValue(key, out DirectoryUser? known) && known is not null)
        {
            return known;
        }

        var found = await admin.FindUserByUsernameAsync(accountName, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            return found.Error;
        }

        if (KeycloakUserMapper.IsServiceAccount(found.Value))
        {
            return Error.NotFound("The account is not a person in the directory.");
        }

        var user = KeycloakUserMapper.ToDirectoryUser(found.Value);
        cache.Set(key, user, Lifetime);
        return user;
    }
}
