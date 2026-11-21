using System.Security.Cryptography;
using System.Text;

using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Subscriptions;

/// <summary>
/// AES-256-GCM sealing of a subscription's refresh token, bound to the subscription id (associated data), so a
/// sealed value copied to another row does not open. Layout: key id (4) ‖ nonce (12) ‖ tag (16) ‖ ciphertext.
/// The key id is the first four bytes of SHA-256(key), so a rotated key is told apart from tampering.
/// </summary>
internal sealed class CredentialSealer
{
    private const int KeyIdSize = 4;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;
    private readonly byte[] _keyId;

    public CredentialSealer(string hexKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hexKey);
        var bytes = Convert.FromHexString(hexKey.Trim());
        if (bytes.Length < 32)
        {
            throw new ArgumentException("The sealing key must be at least 32 bytes (64 hex digits).", nameof(hexKey));
        }

        _key = SHA256.HashData(bytes);
        _keyId = SHA256.HashData(_key)[..KeyIdSize];
    }

    public byte[] Seal(string subscriptionId, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var plain = Encoding.UTF8.GetBytes(secret);
        var sealedValue = new byte[KeyIdSize + NonceSize + TagSize + plain.Length];
        _keyId.CopyTo(sealedValue, 0);
        var nonce = sealedValue.AsSpan(KeyIdSize, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, sealedValue.AsSpan(KeyIdSize + NonceSize + TagSize), sealedValue.AsSpan(KeyIdSize + NonceSize, TagSize), Encoding.UTF8.GetBytes(subscriptionId));
        return sealedValue;
    }

    /// <summary>The secret, or <see cref="ErrorKind.Unauthorized"/> when it was sealed under another key or altered.</summary>
    public Result<string> Open(string subscriptionId, byte[] sealedValue)
    {
        ArgumentNullException.ThrowIfNull(sealedValue);
        if (sealedValue.Length < KeyIdSize + NonceSize + TagSize || !sealedValue.AsSpan(0, KeyIdSize).SequenceEqual(_keyId))
        {
            return Unopenable();
        }

        var plain = new byte[sealedValue.Length - KeyIdSize - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(sealedValue.AsSpan(KeyIdSize, NonceSize), sealedValue.AsSpan(KeyIdSize + NonceSize + TagSize), sealedValue.AsSpan(KeyIdSize + NonceSize, TagSize), plain,
                Encoding.UTF8.GetBytes(subscriptionId));
            return Encoding.UTF8.GetString(plain);
        }
        catch (AuthenticationTagMismatchException)
        {
            return Unopenable();
        }
    }

    private static Error Unopenable() =>
        new(ErrorKind.Unauthorized, "credentialUnavailable", "The subscription's credential cannot be opened; the subscriber must reauthorize.");
}
