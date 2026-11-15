using System.Buffers.Binary;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Nightware.Api.Auth;

/// <summary>
/// The cryptography of self-authorising URLs (ADR 0001 §39): seals a small JSON payload — typically the
/// caller's access token — into a URL-safe string bound to the one request it may authorise.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>AES-256-GCM. The key is HKDF-SHA256 of <c>DeltaLinks:SigningKey</c> (shared by every instance) with
/// the caller's purpose string as <c>info</c>, so each kind of URL has a key of its own and none equals the
/// delta-link HMAC key. With no key configured a random per-process secret is used, as for delta links:
/// URLs then die with the process.</item>
/// <item>Wire form: <c>base64url(version ‖ nonce(12) ‖ tag(16) ‖ ciphertext)</c>, a fresh random nonce per
/// seal. The associated data is the version and the binding fields, each length-prefixed, so a value opens
/// only for the request it was minted for and any change to it fails authentication.</item>
/// <item>Failure is a bare <c>false</c>: tampered, truncated, wrong key and wrong binding are
/// indistinguishable to the caller. Expiry is the payload owner's business.</item>
/// </list>
/// </remarks>
internal sealed class SelfAuthorisingUrlSealer
{
    /// <summary>Longest sealed value accepted (a 4 KB access token fits several times over).</summary>
    public const int MaxSealedLength = 6000;

    private const byte Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int HeaderSize = 1 + NonceSize + TagSize;

    private static readonly Lazy<byte[]> EphemeralSecret = new(() => RandomNumberGenerator.GetBytes(KeySize));

    private readonly byte[] _key;

    /// <param name="signingKey"><c>DeltaLinks:SigningKey</c>; empty for a per-process key.</param>
    /// <param name="purpose">Names the kind of URL (e.g. <c>nightware/todo-upload-url/v1</c>); keys never cross purposes.</param>
    public SelfAuthorisingUrlSealer(string? signingKey, string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        var secret = string.IsNullOrWhiteSpace(signingKey) ? EphemeralSecret.Value : Encoding.UTF8.GetBytes(signingKey);
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, KeySize, salt: [], info: Encoding.UTF8.GetBytes(purpose));
    }

    public string Seal<T>(T payload, JsonTypeInfo<T> type, IReadOnlyList<string> boundTo)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(boundTo);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, type);
        var sealedBytes = new byte[HeaderSize + plaintext.Length];
        sealedBytes[0] = Version;
        var nonce = sealedBytes.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, sealedBytes.AsSpan(HeaderSize), sealedBytes.AsSpan(1 + NonceSize, TagSize), AssociatedData(boundTo));
        CryptographicOperations.ZeroMemory(plaintext);
        return Base64Url.EncodeToString(sealedBytes);
    }

    /// <summary>The payload, when <paramref name="sealedValue"/> was sealed by this key and purpose for exactly <paramref name="boundTo"/>.</summary>
    public bool TryOpen<T>(string? sealedValue, JsonTypeInfo<T> type, IReadOnlyList<string> boundTo, [NotNullWhen(true)] out T? payload)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(boundTo);
        payload = null;
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
        try
        {
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(sealedBytes.AsSpan(1, NonceSize), sealedBytes.AsSpan(HeaderSize), sealedBytes.AsSpan(1 + NonceSize, TagSize), plaintext, AssociatedData(boundTo));
            payload = JsonSerializer.Deserialize(plaintext, type);
            return payload is not null;
        }
        catch (Exception e) when (e is AuthenticationTagMismatchException or CryptographicException or JsonException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] AssociatedData(IReadOnlyList<string> fields)
    {
        var encoded = fields.Select(Encoding.UTF8.GetBytes).ToArray();
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
}
