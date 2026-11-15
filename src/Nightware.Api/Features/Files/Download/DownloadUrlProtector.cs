using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;

using Nightware.Api.Auth;
using Nightware.Api.Graph.Json;
using Nightware.Domain.Files;

namespace Nightware.Api.Features.Files.Download;

/// <summary>What a download URL is bound to: <c>GET</c> of one share id in one format.</summary>
internal sealed record DownloadUrlBinding(string ShareId, ContentFormat Format);

/// <summary>What a download URL carries, encrypted: the caller's access token and when the URL stops working.</summary>
internal sealed record DownloadTicket(string BearerToken, DateTimeOffset ExpiresAt);

/// <summary>
/// Seals the caller's access token into a share download URL (ADR 0001 §39), with
/// <see cref="SelfAuthorisingUrlSealer"/> under a purpose of its own: an upload URL's credential can never
/// open here, nor this one there. Bound to the method, share id and format, so the URL downloads exactly
/// what the redirecting request was allowed and nothing else.
/// </summary>
internal sealed class DownloadUrlProtector
{
    private const string Method = "GET";
    private const string Purpose = "nightware/share-download-url/v1";

    private readonly SelfAuthorisingUrlSealer _sealer;
    private readonly TimeProvider _clock;

    public DownloadUrlProtector(IOptions<DeltaLinkOptions> options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        _sealer = new SelfAuthorisingUrlSealer(options.Value.SigningKey, Purpose);
        _clock = clock;
    }

    /// <summary>
    /// A ticket for <paramref name="bearerToken"/> that lives <see cref="DownloadUrl.Lifetime"/>, or less when the
    /// token expires sooner: a recovered token never outlives itself.
    /// </summary>
    public DownloadTicket TicketFor(string bearerToken, DateTimeOffset? tokenExpiry)
    {
        var expires = _clock.GetUtcNow() + DownloadUrl.Lifetime;
        return new DownloadTicket(bearerToken, tokenExpiry is { } t && t < expires ? t : expires);
    }

    public string Seal(DownloadTicket ticket, DownloadUrlBinding binding)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(binding);
        return _sealer.Seal(new Payload(ticket.BearerToken, ticket.ExpiresAt.ToUnixTimeSeconds()), DownloadUrlJson.Default.Payload, BoundTo(binding));
    }

    /// <summary>The ticket, when <paramref name="sealedValue"/> was minted by this key for exactly <paramref name="binding"/> and has not expired.</summary>
    public bool TryOpen(string? sealedValue, DownloadUrlBinding binding, out DownloadTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ticket = null!;
        if (!_sealer.TryOpen(sealedValue, DownloadUrlJson.Default.Payload, BoundTo(binding), out var payload)
            || string.IsNullOrEmpty(payload.Token)
            || payload.ExpiresAt <= _clock.GetUtcNow().ToUnixTimeSeconds())
        {
            return false;
        }

        ticket = new DownloadTicket(payload.Token, DateTimeOffset.FromUnixTimeSeconds(Math.Min(payload.ExpiresAt, DateTimeOffset.MaxValue.ToUnixTimeSeconds())));
        return true;
    }

    private static string[] BoundTo(DownloadUrlBinding binding) =>
        [Method, binding.ShareId, binding.Format == ContentFormat.Pdf ? "pdf" : string.Empty];

    internal sealed record Payload(
        [property: JsonPropertyName("t")] string Token,
        [property: JsonPropertyName("e")] long ExpiresAt);
}

[JsonSerializable(typeof(DownloadUrlProtector.Payload))]
internal sealed partial class DownloadUrlJson : JsonSerializerContext;
