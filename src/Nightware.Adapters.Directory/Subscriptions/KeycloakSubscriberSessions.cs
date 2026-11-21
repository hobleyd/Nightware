using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

using Nightware.Adapters.Directory.Core;
using Nightware.Adapters.Directory.Credentials;
using Nightware.Application.Subscriptions;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Subscriptions;

/// <summary>
/// <see cref="ISubscriberSessions"/> by Keycloak's refresh grant (ADR 0006 D10). The access token it returns carries
/// <c>aud [stalwart, nextcloud]</c> and exchanges for either backend's token like a request's. An ended session is
/// <see cref="ErrorKind.BackendAuthFailed"/>.
/// </summary>
/// <remarks>
/// A renewed session is cached under SHA-256(refresh token), never the token itself, until its access token is
/// <see cref="ExchangedTokenCacheOptions.ExpirySkew"/> from expiring. A poll in that time reuses the access token and
/// keeps the refresh token it came with, so it costs Keycloak nothing: the backend exchanges of that same access token
/// are cached by <see cref="BackendCredentials"/>.
/// </remarks>
internal sealed class KeycloakSubscriberSessions(
    IKeycloakTokenClient tokens,
    IMemoryCache cache,
    IOptions<BackendCredentialOptions> options,
    TimeProvider timeProvider) : ISubscriberSessions
{
    public async Task<Result<SubscriberSession>> RenewAsync(string refreshToken, CancellationToken cancellationToken)
    {
        ExchangedTokenCacheOptions cacheOptions = options.Value.Cache;
        string key = CacheKey(refreshToken);
        if (cacheOptions.Enabled && cache.TryGetValue(key, out CachedSession? hit) && hit is not null && timeProvider.GetUtcNow() < hit.ValidUntil)
        {
            return hit.Session;
        }

        var pair = await tokens.RefreshAsync(refreshToken, cancellationToken).ConfigureAwait(false);
        if (!pair.IsSuccess)
        {
            return pair.Error;
        }

        var session = new SubscriberSession(pair.Value.Access.Value, pair.Value.RefreshToken);
        DateTimeOffset validUntil = pair.Value.Access.ExpiresAt - cacheOptions.ExpirySkew;
        TimeSpan ttl = validUntil - timeProvider.GetUtcNow();
        if (cacheOptions.Enabled && ttl > TimeSpan.Zero)
        {
            // Under the refresh token the next poll will present: the one this renewal returned.
            cache.Set(CacheKey(session.RefreshToken), new CachedSession(session, validUntil), new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ttl,
                Size = 1,
            });
        }

        return session;
    }

    private static string CacheKey(string refreshToken)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
        return $"Nightware.SubscriberSessions:{hash}";
    }

    private sealed record CachedSession(SubscriberSession Session, DateTimeOffset ValidUntil);
}
