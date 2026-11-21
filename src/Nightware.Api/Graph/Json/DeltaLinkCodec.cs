using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Nightware.Api.Graph.OData;
using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Graph.Json;

/// <summary>Configuration section <c>DeltaLinks</c>.</summary>
public sealed class DeltaLinkOptions
{
    public const string SectionName = "DeltaLinks";

    /// <summary>
    /// HMAC key that signs delta/next links. Must be shared by every instance and survive restarts;
    /// when empty a random per-process key is used (links then die with the process → 410 → resync).
    /// </summary>
    public string? SigningKey { get; set; }

    /// <summary>How long a link stays valid. Graph keeps mail delta tokens for about a week.</summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromDays(30);
}

/// <summary>What a delta link freezes: the adapter's token plus the original <c>$select</c>/<c>$top</c>.</summary>
public sealed record DeltaLinkState(DeltaToken Token, IReadOnlySet<string>? Select, int? Top);

/// <summary>
/// The cursor of one delta request: <see cref="Token"/> is null for an initial sync, and
/// <see cref="Select"/>/<see cref="Top"/> come from the link when following one (Graph freezes them).
/// </summary>
public sealed record DeltaCursor(DeltaToken? Token, IReadOnlySet<string>? Select, int? Top)
{
    public bool IsInitial => Token is null;
}

/// <summary>
/// Wraps an adapter <see cref="DeltaToken"/> with the frozen query options into the opaque
/// <c>$deltatoken</c>/<c>$skiptoken</c> value of an <c>@odata.deltaLink</c>/<c>@odata.nextLink</c>
/// (ADR 0001 §7, architecture.md "Delta").
/// </summary>
/// <remarks>
/// Format: <c>base64url(json) "." base64url(HMAC-SHA256(json))</c>, no padding, so it survives a
/// query string verbatim. Decoding errors are all <see cref="ErrorKind.SyncStateGone"/> (410, the
/// client resyncs): anything undecodable, of an unknown version, older than
/// <see cref="DeltaLinkOptions.Lifetime"/>, or whose signature does not verify. A bad signature is
/// usually a link signed before the key was rotated, and a Graph client resyncs only on 410; a
/// forged link gains nothing, since its payload is never read. <c>$deltatoken</c> and <c>$skiptoken</c> are interchangeable inputs.
/// </remarks>
public sealed partial class DeltaLinkCodec
{
    private const int Version = 1;
    private readonly byte[] _key;
    private readonly TimeSpan _lifetime;
    private readonly TimeProvider _clock;

    public DeltaLinkCodec(IOptions<DeltaLinkOptions> options, TimeProvider clock, ILogger<DeltaLinkCodec> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _clock = clock;
        _lifetime = options.Value.Lifetime;
        if (string.IsNullOrWhiteSpace(options.Value.SigningKey))
        {
            LogEphemeralKey(logger);
            _key = RandomNumberGenerator.GetBytes(32);
        }
        else
        {
            _key = Encoding.UTF8.GetBytes(options.Value.SigningKey);
        }
    }

    public string Encode(DeltaLinkState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var payload = new Payload
        {
            V = Version,
            T = state.Token.Value,
            S = state.Select?.ToArray(),
            N = state.Top,
            I = _clock.GetUtcNow().ToUnixTimeSeconds(),
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, PayloadJson);
        return $"{Base64Url.EncodeToString(json)}.{Base64Url.EncodeToString(HMACSHA256.HashData(_key, json))}";
    }

    public Result<DeltaLinkState> Decode(string? link)
    {
        var dot = link?.IndexOf('.', StringComparison.Ordinal) ?? -1;
        if (link is null || dot <= 0 || dot == link.Length - 1)
        {
            return Gone();
        }

        byte[] json;
        byte[] signature;
        try
        {
            json = Base64Url.DecodeFromChars(link.AsSpan(0, dot));
            signature = Base64Url.DecodeFromChars(link.AsSpan(dot + 1));
        }
        catch (FormatException)
        {
            return Gone();
        }

        if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(_key, json), signature))
        {
            return Gone();
        }

        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(json, PayloadJson);
        }
        catch (JsonException)
        {
            return Gone();
        }

        if (payload is null || payload.V != Version || string.IsNullOrEmpty(payload.T))
        {
            return Gone();
        }

        var issued = DateTimeOffset.FromUnixTimeSeconds(payload.I);
        if (_clock.GetUtcNow() - issued > _lifetime)
        {
            return Error.SyncStateGone("The delta token has expired.");
        }

        IReadOnlySet<string>? select = payload.S is null ? null : new HashSet<string>(payload.S, StringComparer.OrdinalIgnoreCase);
        return new DeltaLinkState(new DeltaToken(payload.T), select, payload.N);
    }

    /// <summary>
    /// The cursor for a delta request: decodes <c>$deltatoken</c> or <c>$skiptoken</c> when present
    /// (their frozen <c>$select</c>/<c>$top</c> win), otherwise starts an initial sync with the request's own options.
    /// </summary>
    public Result<DeltaCursor> ReadCursor(IQueryCollection query, ItemQuery parsed)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(parsed);
        var link = query[ODataQueryParser.DeltaTokenOption].ToString();
        if (string.IsNullOrEmpty(link))
        {
            link = query[ODataQueryParser.SkipTokenOption].ToString();
        }

        if (string.IsNullOrEmpty(link))
        {
            return new DeltaCursor(null, parsed.Select, parsed.Top);
        }

        return Decode(link).Map(static s => new DeltaCursor(s.Token, s.Select, s.Top));
    }

    private static Error Gone() => Error.SyncStateGone("The delta token is not valid; start a new sync.");

    private static readonly JsonSerializerOptions PayloadJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "DeltaLinks:SigningKey is not configured; using a per-process key. Delta links will not survive a restart or work across instances.")]
    private static partial void LogEphemeralKey(ILogger logger);

    private sealed class Payload
    {
        [JsonPropertyName("v")]
        public int V { get; init; }

        [JsonPropertyName("t")]
        public string? T { get; init; }

        [JsonPropertyName("s")]
        public string[]? S { get; init; }

        [JsonPropertyName("n")]
        public int? N { get; init; }

        [JsonPropertyName("i")]
        public long I { get; init; }
    }
}
