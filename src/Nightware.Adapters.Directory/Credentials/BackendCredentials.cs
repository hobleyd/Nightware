using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Nightware.Adapters.Directory.Core;
using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Credentials;

/// <summary>
/// <see cref="IBackendCredentials"/> for the current request (scoped: it reads the per-request
/// <see cref="IUserContext"/>). Strategy per backend comes from <see cref="BackendCredentialOptions"/>;
/// <see cref="Backend.Directory"/> always uses the service account (ADR 0001 §17).
/// </summary>
/// <remarks>
/// Exchanged tokens are cached in <see cref="IMemoryCache"/> under SHA-256(subject token) + backend +
/// audience, never the token itself, until min(exchanged exp − skew, subject exp). The expiry is also
/// stored in the entry and checked against <see cref="TimeProvider"/> on read.
/// </remarks>
internal sealed partial class BackendCredentials(
    IUserContext userContext,
    IKeycloakTokenClient tokens,
    IMemoryCache cache,
    IOptions<BackendCredentialOptions> options,
    TimeProvider timeProvider,
    ILogger<BackendCredentials> logger) : IBackendCredentials
{
    public async Task<Result<BackendCredential>> ForAsync(Backend backend, CancellationToken cancellationToken)
    {
        if (backend == Backend.Directory)
        {
            return await ServiceAccountBearerAsync(actAs: null, cancellationToken).ConfigureAwait(false);
        }

        BackendCredentialPolicy policy = options.Value.For(backend)
            ?? throw new ArgumentOutOfRangeException(nameof(backend), backend, "Unknown backend.");

        return policy.Strategy switch
        {
            CredentialStrategy.PassThrough => PassThrough(),
            CredentialStrategy.TokenExchange => await ExchangeAsync(backend, policy.Audience!, cancellationToken).ConfigureAwait(false),
            CredentialStrategy.ServiceAccount => await ServiceAccountAsync(policy.ServiceAccount, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported credential strategy {policy.Strategy}."),
        };
    }

    private Result<BackendCredential> PassThrough() =>
        SubjectToken() is { } bearer
            ? new BackendCredential("Bearer", bearer, CredentialStrategy.PassThrough)
            : NoCallerToken();

    private async Task<Result<BackendCredential>> ExchangeAsync(Backend backend, string audience, CancellationToken cancellationToken)
    {
        if (SubjectToken() is not { } subject)
        {
            return NoCallerToken();
        }

        ExchangedTokenCacheOptions cacheOptions = options.Value.Cache;
        string key = CacheKey(backend, audience, subject);
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (cacheOptions.Enabled && cache.TryGetValue(key, out CachedCredential? hit) && hit is not null && now < hit.ValidUntil)
        {
            LogCacheHit(logger, backend);
            return hit.Credential;
        }

        Result<AccessToken> exchanged = await tokens.ExchangeAsync(subject, audience, cancellationToken).ConfigureAwait(false);
        if (!exchanged.IsSuccess)
        {
            return exchanged.Error;
        }

        BackendCredential credential = new("Bearer", exchanged.Value.Value, CredentialStrategy.TokenExchange);
        if (cacheOptions.Enabled)
        {
            DateTimeOffset validUntil = exchanged.Value.ExpiresAt - cacheOptions.ExpirySkew;
            if (JwtExpiry.Read(subject) is { } subjectExpiry && subjectExpiry < validUntil)
            {
                validUntil = subjectExpiry;
            }

            TimeSpan ttl = validUntil - timeProvider.GetUtcNow();
            if (ttl > TimeSpan.Zero)
            {
                cache.Set(key, new CachedCredential(credential, validUntil), new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = ttl,
                    Size = 1,
                });
            }
        }

        LogExchanged(logger, backend, audience, exchanged.Value.ExpiresAt);
        return credential;
    }

    private async Task<Result<BackendCredential>> ServiceAccountAsync(ServiceAccountCredentialOptions account, CancellationToken cancellationToken)
    {
        string? actAs = string.IsNullOrWhiteSpace(userContext.Principal.Mail) ? null : userContext.Principal.Mail;
        if (account.Mode == ServiceAccountMode.ClientCredentials)
        {
            return await ServiceAccountBearerAsync(actAs, cancellationToken).ConfigureAwait(false);
        }

        string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{account.Username}:{account.Password}"));
        return new BackendCredential("Basic", basic, CredentialStrategy.ServiceAccount, actAs);
    }

    private async Task<Result<BackendCredential>> ServiceAccountBearerAsync(string? actAs, CancellationToken cancellationToken)
    {
        Result<AccessToken> token = await tokens.GetServiceAccountTokenAsync(cancellationToken).ConfigureAwait(false);
        return token.Map(t => new BackendCredential("Bearer", t.Value, CredentialStrategy.ServiceAccount, actAs));
    }

    private string? SubjectToken() => string.IsNullOrWhiteSpace(userContext.BearerToken) ? null : userContext.BearerToken;

    private static Error NoCallerToken() =>
        new(ErrorKind.Unauthorized, "InvalidAuthenticationToken", "The request carries no access token.");

    private static string CacheKey(Backend backend, string audience, string subject)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(subject)));
        return $"Nightware.BackendCredentials:{backend}:{audience}:{hash}";
    }

    private sealed record CachedCredential(BackendCredential Credential, DateTimeOffset ValidUntil);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Exchanged the caller's token for {Backend} (audience {Audience}), expires {ExpiresAt:O}")]
    private static partial void LogExchanged(ILogger logger, Backend backend, string audience, DateTimeOffset expiresAt);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Exchanged token for {Backend} served from cache")]
    private static partial void LogCacheHit(ILogger logger, Backend backend);
}
