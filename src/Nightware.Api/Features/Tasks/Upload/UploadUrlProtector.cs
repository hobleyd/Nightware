using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;

using Nightware.Api.Auth;
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
/// <item>The cryptography is <see cref="SelfAuthorisingUrlSealer"/>'s, under this URL's own purpose
/// string. The plaintext is the token, file name, size and expiry; the binding is the method and the
/// route's mailbox (case-insensitively), list, task and session, so a URL works only for the request
/// it was minted for.</item>
/// <item>The expiry is the earlier of the session's and the access token's: a recovered token gets
/// no extra life, and normal JWT validation still runs on it afterwards.</item>
/// <item>Failure is a bare <c>false</c>: tampered, truncated, expired, wrong key and wrong route
/// are indistinguishable to the caller.</item>
/// </list>
/// </remarks>
internal sealed class UploadUrlProtector
{
    private const string Method = "PUT";

    /// <summary>Longest sealed value accepted (a 4 KB access token fits several times over).</summary>
    internal const int MaxSealedLength = SelfAuthorisingUrlSealer.MaxSealedLength;

    private const string Purpose = "nightware/todo-upload-url/v1";

    private readonly SelfAuthorisingUrlSealer _sealer;
    private readonly TimeProvider _clock;

    public UploadUrlProtector(IOptions<DeltaLinkOptions> options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        _sealer = new SelfAuthorisingUrlSealer(options.Value.SigningKey, Purpose);
        _clock = clock;
    }

    public string Seal(UploadTicket ticket, UploadUrlBinding binding)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(binding);
        return _sealer.Seal(Payload.From(ticket), UploadUrlJson.Default.Payload, BoundTo(binding));
    }

    /// <summary>The ticket, when <paramref name="sealedValue"/> was minted by this key for exactly <paramref name="binding"/> and has not expired.</summary>
    public bool TryOpen(string? sealedValue, UploadUrlBinding binding, out UploadTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ticket = null!;
        if (!_sealer.TryOpen(sealedValue, UploadUrlJson.Default.Payload, BoundTo(binding), out var payload)
            || string.IsNullOrEmpty(payload.Token))
        {
            return false;
        }

        DateTimeOffset expiresAt;
        try
        {
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAt);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        if (expiresAt <= _clock.GetUtcNow())
        {
            return false;
        }

        ticket = new UploadTicket(payload.Token, payload.Name, payload.Size, expiresAt);
        return true;
    }

    private static string[] BoundTo(UploadUrlBinding binding) =>
        [Method, binding.Mailbox.ToLowerInvariant(), binding.ListId, binding.TaskId, binding.SessionId];

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
