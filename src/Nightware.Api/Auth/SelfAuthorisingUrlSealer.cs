using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;

using Nightware.Api.Graph.Json;

namespace Nightware.Api.Auth;

/// <summary>
/// What a self-authorising URL carries, encrypted: the caller's access token, when the URL stops working and,
/// for an upload URL, the one file it may carry.
/// </summary>
public sealed record SealedTicket(string BearerToken, DateTimeOffset ExpiresAt)
{
    /// <summary>The file name an upload URL was opened for.</summary>
    public string? Name { get; init; }

    /// <summary>The byte count an upload URL was opened for.</summary>
    public long? Size { get; init; }
}

/// <summary>
/// Mints and opens the credential of self-authorising URLs (ADR 0001 §39): a <see cref="SealedTicket"/> sealed
/// into a URL-safe string bound to the one request it may authorise.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>AES-256-GCM. The key is HKDF-SHA256 of <c>DeltaLinks:SigningKey</c> (shared by every instance) with the
/// kind's <see cref="SelfAuthorisingUrl.Purpose"/> as <c>info</c>, so each kind of URL has a key of its own and
/// none equals the delta-link HMAC key. With no key configured a random per-process secret is used, as for
/// delta links: URLs then die with the process.</item>
/// <item>Wire form: <c>base64url(version ‖ nonce(12) ‖ tag(16) ‖ ciphertext)</c>, a fresh random nonce per seal.
/// The plaintext is JSON <c>{t, e[, n, s]}</c>. The associated data is the version, the kind's method and the
/// binding fields, each length-prefixed, so a value opens only for the request it was minted for.</item>
/// <item>The expiry is checked here, on open: a ticket lives the kind's lifetime, or less when the access token
/// (or the caller's <c>notAfter</c>) ends sooner, so a recovered token never outlives itself. Normal JWT
/// validation still runs on it afterwards.</item>
/// <item>Failure is a bare <c>false</c>: tampered, truncated, expired, wrong key and wrong binding are
/// indistinguishable to the caller.</item>
/// </list>
/// </remarks>
public sealed class SelfAuthorisingUrlSealer
{
    /// <summary>Longest sealed value accepted (a 4 KB access token fits several times over).</summary>
    public const int MaxSealedLength = 6000;

    private const byte Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int HeaderSize = 1 + NonceSize + TagSize;

    private static readonly Lazy<byte[]> EphemeralSecret = new(() => RandomNumberGenerator.GetBytes(KeySize));

    private readonly byte[] _secret;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, byte[]> _keys = new(StringComparer.Ordinal);

    public SelfAuthorisingUrlSealer(IOptions<DeltaLinkOptions> options, TimeProvider clock)
        : this(options?.Value.SigningKey, clock)
    {
    }

    /// <param name="signingKey"><c>DeltaLinks:SigningKey</c>; empty for a per-process key.</param>
    internal SelfAuthorisingUrlSealer(string? signingKey, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _secret = string.IsNullOrWhiteSpace(signingKey) ? EphemeralSecret.Value : Encoding.UTF8.GetBytes(signingKey);
        _clock = clock;
    }

    /// <summary>
    /// A ticket for <paramref name="bearerToken"/> that lives <paramref name="kind"/>'s lifetime, or less when
    /// the caller's validated token (<c>exp</c>) or <paramref name="notAfter"/> ends sooner.
    /// </summary>
    public SealedTicket TicketFor(SelfAuthorisingUrl kind, string bearerToken, ClaimsPrincipal caller, DateTimeOffset? notAfter = null)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentException.ThrowIfNullOrEmpty(bearerToken);
        ArgumentNullException.ThrowIfNull(caller);
        var expires = _clock.GetUtcNow() + kind.Lifetime;
        foreach (var limit in new[] { ExpiryOf(caller), notAfter })
        {
            if (limit is { } l && l < expires)
            {
                expires = l;
            }
        }

        return new SealedTicket(bearerToken, expires);
    }

    public string Seal(SelfAuthorisingUrl kind, SealedTicket ticket, IReadOnlyList<string> boundTo)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(boundTo);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(Payload.From(ticket), SealedTicketJson.Default.Payload);
        var sealedBytes = new byte[HeaderSize + plaintext.Length];
        sealedBytes[0] = Version;
        var nonce = sealedBytes.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(KeyFor(kind), TagSize);
        aes.Encrypt(nonce, plaintext, sealedBytes.AsSpan(HeaderSize), sealedBytes.AsSpan(1 + NonceSize, TagSize), AssociatedData(kind, boundTo));
        CryptographicOperations.ZeroMemory(plaintext);
        return Base64Url.EncodeToString(sealedBytes);
    }

    /// <summary>The ticket, when <paramref name="sealedValue"/> was sealed by this key for <paramref name="kind"/> and exactly <paramref name="boundTo"/>, and has not expired.</summary>
    public bool TryOpen(SelfAuthorisingUrl kind, string? sealedValue, IReadOnlyList<string> boundTo, [NotNullWhen(true)] out SealedTicket? ticket)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(boundTo);
        ticket = null;
        if (string.IsNullOrEmpty(sealedValue) || sealedValue.Length > MaxSealedLength || !Base64Url.IsValid(sealedValue))
        {
            return false;
        }

        var sealedBytes = Base64Url.DecodeFromChars(sealedValue);
        if (sealedBytes.Length <= HeaderSize || sealedBytes[0] != Version)
        {
            return false;
        }

        var plaintext = new byte[sealedBytes.Length - HeaderSize];
        Payload? payload;
        try
        {
            using var aes = new AesGcm(KeyFor(kind), TagSize);
            aes.Decrypt(sealedBytes.AsSpan(1, NonceSize), sealedBytes.AsSpan(HeaderSize), sealedBytes.AsSpan(1 + NonceSize, TagSize), plaintext, AssociatedData(kind, boundTo));
            payload = JsonSerializer.Deserialize(plaintext, SealedTicketJson.Default.Payload);
        }
        catch (Exception e) when (e is AuthenticationTagMismatchException or CryptographicException or JsonException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        if (payload is not { Token.Length: > 0 }
            || payload.ExpiresAt <= _clock.GetUtcNow().ToUnixTimeSeconds()
            || payload.ExpiresAt > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return false;
        }

        ticket = new SealedTicket(payload.Token, DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAt)) { Name = payload.Name, Size = payload.Size };
        return true;
    }

    /// <summary>The validated access token's <c>exp</c> (raw OIDC claims: <c>MapInboundClaims = false</c>).</summary>
    internal static DateTimeOffset? ExpiryOf(ClaimsPrincipal caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return long.TryParse(caller.FindFirst("exp")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
               && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }

    private byte[] KeyFor(SelfAuthorisingUrl kind) =>
        _keys.GetOrAdd(kind.Purpose, static (purpose, secret) =>
            HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, KeySize, salt: [], info: Encoding.UTF8.GetBytes(purpose)), _secret);

    private static byte[] AssociatedData(SelfAuthorisingUrl kind, IReadOnlyList<string> boundTo)
    {
        var encoded = boundTo.Prepend(kind.Method).Select(Encoding.UTF8.GetBytes).ToArray();
        var data = new byte[1 + encoded.Sum(f => 4 + f.Length)];
        data[0] = Version;
        var offset = 1;
        foreach (var field in encoded)
        {
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset), field.Length);
            field.CopyTo(data, offset + 4);
            offset += 4 + field.Length;
        }

        return data;
    }

    internal sealed record Payload(
        [property: JsonPropertyName("t")] string Token,
        [property: JsonPropertyName("e")] long ExpiresAt)
    {
        [JsonPropertyName("n")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; init; }

        [JsonPropertyName("s")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? Size { get; init; }

        public static Payload From(SealedTicket ticket) =>
            new(ticket.BearerToken, ticket.ExpiresAt.ToUnixTimeSeconds()) { Name = ticket.Name, Size = ticket.Size };
    }
}

[JsonSerializable(typeof(SelfAuthorisingUrlSealer.Payload))]
internal sealed partial class SealedTicketJson : JsonSerializerContext;
