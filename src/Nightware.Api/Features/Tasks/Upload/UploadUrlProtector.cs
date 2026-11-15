using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Nightware.Api.Graph.Json;

namespace Nightware.Api.Features.Tasks.Upload;

/// <summary>What an upload URL is bound to: the one route it may be PUT to (method, mailbox, list, task, session).</summary>
internal sealed record UploadUrlBinding(string Mailbox, string ListId, string TaskId, string SessionId);

/// <summary>What an upload URL carries, encrypted: the caller's access token and the one file it authorises.</summary>
internal sealed record UploadTicket(string BearerToken, string Name, long Size, DateTimeOffset ExpiresAt);

/// <summary>
/// Seals the caller's access token into a To Do <c>uploadUrl</c> (ADR 0001 §39). NightMail PUTs the
/// file to that URL with no <c>Authorization</c> header, so the URL itself must carry — and protect —
/// the credential.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>AES-256-GCM. The key is HKDF-SHA256 of <c>DeltaLinks:SigningKey</c> (shared by every
/// instance) with a purpose string of its own, so it never equals the delta-link HMAC key. With no
/// key configured a random per-process key is used, as for delta links: URLs then die with the process.</item>
/// <item>Wire form: <c>base64url(version ‖ nonce(12) ‖ tag(16) ‖ ciphertext)</c>, a fresh random nonce
/// per URL. The plaintext is the token, file name, size and expiry; the associated data is the
/// version, the method and the route's mailbox, list, task and session (each length-prefixed), so a
/// URL works only for the request it was minted for and any change to it fails authentication.</item>
/// <item>The expiry is the earlier of the session's and the access token's: a recovered token gets
/// no extra life, and normal JWT validation still runs on it afterwards.</item>
/// <item>Failure is a bare <c>false</c>: tampered, truncated, expired, wrong key and wrong route
/// are indistinguishable to the caller.</item>
/// </list>
/// </remarks>
internal sealed class UploadUrlProtector
{
    private const byte Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int HeaderSize = 1 + NonceSize + TagSize;
    private const string Method = "PUT";

    /// <summary>Longest sealed value accepted (a 4 KB access token fits several times over).</summary>
    internal const int MaxSealedLength = 6000;

    private static readonly byte[] Purpose = "nightware/todo-upload-url/v1"u8.ToArray();
    private static readonly Lazy<byte[]> EphemeralSecret = new(() => RandomNumberGenerator.GetBytes(KeySize));

    private readonly byte[] _key;
    private readonly TimeProvider _clock;

    public UploadUrlProtector(IOptions<DeltaLinkOptions> options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        var secret = string.IsNullOrWhiteSpace(options.Value.SigningKey)
            ? EphemeralSecret.Value
            : Encoding.UTF8.GetBytes(options.Value.SigningKey);
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, KeySize, salt: [], info: Purpose);
        _clock = clock;
    }

    public static UploadUrlProtector Create(IServiceProvider services) =>
        new(services.GetRequiredService<IOptions<DeltaLinkOptions>>(), services.GetRequiredService<TimeProvider>());

    public string Seal(UploadTicket ticket, UploadUrlBinding binding)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(binding);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(Payload.From(ticket), UploadUrlJson.Default.Payload);
        var sealedBytes = new byte[HeaderSize + plaintext.Length];
        sealedBytes[0] = Version;
        var nonce = sealedBytes.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, sealedBytes.AsSpan(HeaderSize), sealedBytes.AsSpan(1 + NonceSize, TagSize), AssociatedData(binding));
        CryptographicOperations.ZeroMemory(plaintext);
        return Base64Url.EncodeToString(sealedBytes);
    }

    /// <summary>The ticket, when <paramref name="sealedValue"/> was minted by this key for exactly <paramref name="binding"/> and has not expired.</summary>
    public bool TryOpen(string? sealedValue, UploadUrlBinding binding, out UploadTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ticket = null!;
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
            aes.Decrypt(sealedBytes.AsSpan(1, NonceSize), sealedBytes.AsSpan(HeaderSize), sealedBytes.AsSpan(1 + NonceSize, TagSize), plaintext, AssociatedData(binding));
            var payload = JsonSerializer.Deserialize(plaintext, UploadUrlJson.Default.Payload);
            if (payload is null || string.IsNullOrEmpty(payload.Token) || DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAt) <= _clock.GetUtcNow())
            {
                return false;
            }

            ticket = new UploadTicket(payload.Token, payload.Name, payload.Size, DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAt));
            return true;
        }
        catch (Exception e) when (e is AuthenticationTagMismatchException or CryptographicException or JsonException or ArgumentOutOfRangeException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] AssociatedData(UploadUrlBinding binding)
    {
        string[] fields = [Method, binding.Mailbox.ToLowerInvariant(), binding.ListId, binding.TaskId, binding.SessionId];
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

    internal sealed record Payload(
        [property: JsonPropertyName("t")] string Token,
        [property: JsonPropertyName("n")] string Name,
        [property: JsonPropertyName("s")] long Size,
        [property: JsonPropertyName("e")] long ExpiresAt)
    {
        public static Payload From(UploadTicket ticket) => new(ticket.BearerToken, ticket.Name, ticket.Size, ticket.ExpiresAt.ToUnixTimeSeconds());
    }
}

[JsonSerializable(typeof(UploadUrlProtector.Payload))]
internal sealed partial class UploadUrlJson : JsonSerializerContext;
