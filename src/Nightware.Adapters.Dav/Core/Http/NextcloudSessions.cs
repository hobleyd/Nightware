using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace Nightware.Adapters.Dav.Core.Http;

/// <summary>
/// Nextcloud's session cookies, one set per user. Nextcloud signs a bearer request in from scratch unless the
/// request's PHP session already belongs to that user, and every such sign-in stores a new "remember me" session
/// token (an RSA keypair and two rows). A cookie-free client therefore minted one token per request: thousands a
/// day per user from the change-stream poll alone. Sending each user's own session back stops that.
/// </summary>
/// <remarks>
/// The key is the bearer token's issuer and subject, never a jar shared across users: Nextcloud's session is the
/// user's identity, and it wins over the bearer (verified against Nextcloud 35: bob's token with alice's session
/// cookie reads alice's DAV tree). The token is only read here, not validated, which is safe because every token
/// that reaches this point came from Nightware's own credential pipeline: the caller's token after the façade's JWT
/// validation, or Keycloak's token exchange. An entry is dropped after <see cref="IdleTimeout"/> unused or when
/// Nextcloud answers 401.
/// </remarks>
public sealed class NextcloudSessions : IDisposable
{
    /// <summary>How long a user's session is kept without a request (Nextcloud's own default lifetime is a day).</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Upper bound on users held at once; beyond it the cache compacts.</summary>
    public const int MaxUsers = 10_000;

    private readonly MemoryCache _cache;
    private readonly TimeProvider _time;

    public NextcloudSessions(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = MaxUsers, Clock = new Clock(time) });
    }

    /// <summary>
    /// The cache key for a request's credential, or <see langword="null"/> when it carries none worth a session
    /// (no header, or not a bearer token). Hashed, so the cache never holds a token.
    /// </summary>
    public static string? KeyFor(Uri origin, AuthenticationHeaderValue? authorization)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (authorization is null
            || !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(authorization.Parameter))
        {
            return null;
        }

        // An opaque (non-JWT) token falls back to the token itself: one session per token, still far fewer sign-ins.
        var identity = Subject(authorization.Parameter) ?? "token\n" + authorization.Parameter;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(origin.GetLeftPart(UriPartial.Authority) + "\n" + identity));
        return Convert.ToHexString(bytes);
    }

    /// <summary>The <c>Cookie</c> header value to send for <paramref name="key"/>, or <see langword="null"/> if none is held.</summary>
    public string? CookieHeader(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (!_cache.TryGetValue(key, out Jar? jar) || jar is null)
        {
            return null;
        }

        return jar.Header();
    }

    /// <summary>Takes in the response's <c>Set-Cookie</c> headers for <paramref name="key"/>.</summary>
    public void Remember(string key, HttpResponseMessage response)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(response);
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return;
        }

        var jar = _cache.GetOrCreate(key, entry =>
        {
            entry.SlidingExpiration = IdleTimeout;
            entry.Size = 1;
            return new Jar();
        })!;
        var now = _time.GetUtcNow();
        foreach (var value in values)
        {
            jar.Apply(value, now);
        }
    }

    /// <summary>Forgets <paramref name="key"/>'s session (Nextcloud refused it, or the user signed out).</summary>
    public void Forget(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        _cache.Remove(key);
    }

    public void Dispose() => _cache.Dispose();

    private static string? Subject(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
            using var json = JsonDocument.Parse(Convert.FromBase64String(payload));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("sub", out var sub) || sub.ValueKind != JsonValueKind.String
                || sub.GetString() is not { Length: > 0 } subject)
            {
                return null;
            }

            var issuer = root.TryGetProperty("iss", out var iss) && iss.ValueKind == JsonValueKind.String ? iss.GetString() : "";
            return "sub\n" + issuer + "\n" + subject;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    /// <summary>One user's cookies by name. Path, domain and Secure are ignored: every request goes to the same Nextcloud.</summary>
    private sealed class Jar
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

        public string? Header()
        {
            lock (_gate)
            {
                return _cookies.Count == 0 ? null : string.Join("; ", _cookies.Select(c => c.Key + "=" + c.Value));
            }
        }

        public void Apply(string setCookie, DateTimeOffset now)
        {
            var attributes = setCookie.Split(';');
            var pair = attributes[0];
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                return;
            }

            var name = pair[..equals].Trim();
            var value = pair[(equals + 1)..].Trim();
            var expired = value.Length == 0 || value == "deleted";
            foreach (var attribute in attributes.Skip(1))
            {
                var (key, argument) = Split(attribute);
                if (key.Equals("max-age", StringComparison.OrdinalIgnoreCase)
                    && long.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxAge))
                {
                    expired |= maxAge <= 0;
                }
                else if (key.Equals("expires", StringComparison.OrdinalIgnoreCase)
                    && DateTimeOffset.TryParse(argument, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expires))
                {
                    expired |= expires <= now;
                }
            }

            lock (_gate)
            {
                if (expired)
                {
                    _cookies.Remove(name);
                }
                else
                {
                    _cookies[name] = value;
                }
            }
        }

        private static (string Key, string Argument) Split(string attribute)
        {
            var equals = attribute.IndexOf('=', StringComparison.Ordinal);
            return equals < 0 ? (attribute.Trim(), "") : (attribute[..equals].Trim(), attribute[(equals + 1)..].Trim());
        }
    }

    private sealed class Clock(TimeProvider time) : Microsoft.Extensions.Internal.ISystemClock
    {
        public DateTimeOffset UtcNow => time.GetUtcNow();
    }
}
