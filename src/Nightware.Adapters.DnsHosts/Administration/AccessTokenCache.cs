using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.DnsHosts.Administration;

/// <summary>
/// Short-lived access tokens the cloud DNS hosts issue for an entity's credential (Azure, Google), kept in memory
/// until shortly before they expire, so a page that makes several calls signs in once. Keyed by a hash of the
/// credential, never the credential itself. Disposable: losing it costs one sign-in.
/// </summary>
internal sealed class AccessTokenCache(TimeProvider clock)
{
    /// <summary>A token is dropped this long before it expires, so a call never starts with one about to lapse.</summary>
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Until)> _tokens = new(StringComparer.Ordinal);

    /// <param name="signIn">Gets a new token and how long it lasts.</param>
    public async Task<Result<string>> GetAsync(
        DnsProvider provider,
        DnsProviderToken credential,
        Func<CancellationToken, Task<Result<(string Token, TimeSpan Lifetime)>>> signIn,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(signIn);
        string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{provider.Key()}\n{credential.Value}")));
        DateTimeOffset now = clock.GetUtcNow();
        if (_tokens.TryGetValue(key, out var cached) && cached.Until > now)
        {
            return cached.Token;
        }

        Result<(string Token, TimeSpan Lifetime)> issued = await signIn(cancellationToken).ConfigureAwait(false);
        if (!issued.IsSuccess)
        {
            _tokens.TryRemove(key, out _);
            return issued.Error;
        }

        _tokens[key] = (issued.Value.Token, now + issued.Value.Lifetime - Margin);
        return issued.Value.Token;
    }
}
