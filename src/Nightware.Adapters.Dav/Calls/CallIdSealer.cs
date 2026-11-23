using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Options;

namespace Nightware.Adapters.Dav.Calls;

/// <summary>
/// AES-256-GCM sealing of a call's state into its id (ADR 0009 D7), bound to the caller's directory id as associated
/// data: another user's bearer can't replay an id to send the caller's Talk session cookie. Layout, base64url:
/// key id (4) ‖ nonce (12) ‖ tag (16) ‖ ciphertext. The key id (first four bytes of SHA-256 of the derived key) tells a
/// rotated key from tampering, though both open to nothing.
/// </summary>
internal sealed class CallIdSealer
{
    private const int KeyIdSize = 4;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;
    private readonly byte[] _keyId;

    public CallIdSealer(IOptions<CallOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var hex = options.Value.SealingKey;
        ArgumentException.ThrowIfNullOrWhiteSpace(hex);
        var bytes = Convert.FromHexString(hex.Trim());
        if (bytes.Length < 32)
        {
            throw new ArgumentException("Calls:SealingKey must be at least 32 bytes (64 hex digits).", nameof(options));
        }

        // A purpose-bound derivation, so the same hex reused for another key never yields the same AES key.
        _key = HMACSHA256.HashData(bytes, "nightware/call-id/v1"u8);
        _keyId = SHA256.HashData(_key)[..KeyIdSize];
    }

    public string Seal(string callerId, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        ArgumentNullException.ThrowIfNull(payload);
        var sealedValue = new byte[KeyIdSize + NonceSize + TagSize + payload.Length];
        _keyId.CopyTo(sealedValue, 0);
        var nonce = sealedValue.AsSpan(KeyIdSize, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, payload, sealedValue.AsSpan(KeyIdSize + NonceSize + TagSize), sealedValue.AsSpan(KeyIdSize + NonceSize, TagSize), Encoding.UTF8.GetBytes(callerId));
        return Base64Url.EncodeToString(sealedValue);
    }

    /// <summary>The payload; null when the id is malformed, altered, sealed under another key or for another caller.</summary>
    public byte[]? Open(string callerId, string callId)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        if (string.IsNullOrEmpty(callId) || callId.Length > 8192 || !Base64Url.IsValid(callId))
        {
            return null;
        }

        var sealedValue = Base64Url.DecodeFromChars(callId);
        if (sealedValue.Length < KeyIdSize + NonceSize + TagSize || !sealedValue.AsSpan(0, KeyIdSize).SequenceEqual(_keyId))
        {
            return null;
        }

        var plain = new byte[sealedValue.Length - KeyIdSize - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(sealedValue.AsSpan(KeyIdSize, NonceSize), sealedValue.AsSpan(KeyIdSize + NonceSize + TagSize), sealedValue.AsSpan(KeyIdSize + NonceSize, TagSize), plain,
                Encoding.UTF8.GetBytes(callerId));
            return plain;
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
    }
}
