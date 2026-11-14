using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Core;

/// <summary>Which half of the delta algorithm (ADR 0001 §6–7) a token continues.</summary>
internal enum DeltaPhase
{
    /// <summary>Paging <c>Email/query inMailbox</c> by <see cref="JmapDeltaCursor.Position"/>.</summary>
    Initial,

    /// <summary>Following <c>Email/changes</c> from <see cref="JmapDeltaCursor.State"/>.</summary>
    Changes,
}

/// <summary>
/// Backend state behind a mail delta token.
/// In <see cref="DeltaPhase.Initial"/>, <see cref="State"/> is the Email state captured when
/// the initial sync started (so the switch to <c>Email/changes</c> misses nothing) and
/// <see cref="Position"/> is the next query offset. In <see cref="DeltaPhase.Changes"/>,
/// <see cref="State"/> is the <c>sinceState</c>. <see cref="Select"/>/<see cref="Top"/> are
/// optional frozen query options (ADR 0001 §7) for phases that keep them adapter-side.
/// </summary>
internal sealed record JmapDeltaCursor(string MailboxId, DeltaPhase Phase, int? Position, string? State)
{
    public IReadOnlyList<string>? Select { get; init; }

    public int? Top { get; init; }

    public static JmapDeltaCursor Initial(string mailboxId, string? startState, int position = 0) =>
        new(mailboxId, DeltaPhase.Initial, position, startState);

    public static JmapDeltaCursor Changes(string mailboxId, string sinceState) =>
        new(mailboxId, DeltaPhase.Changes, null, sinceState);
}

/// <summary>Wire form of <see cref="JmapDeltaCursor"/>: short keys keep the token small.</summary>
internal sealed record JmapDeltaCursorPayload
{
    [JsonPropertyName("v")]
    public int Version { get; init; }

    [JsonPropertyName("m")]
    public string? MailboxId { get; init; }

    /// <summary><c>i</c> = initial, <c>c</c> = changes.</summary>
    [JsonPropertyName("p")]
    public string? Phase { get; init; }

    [JsonPropertyName("o")]
    public int? Position { get; init; }

    [JsonPropertyName("s")]
    public string? State { get; init; }

    [JsonPropertyName("sel")]
    public IReadOnlyList<string>? Select { get; init; }

    [JsonPropertyName("top")]
    public int? Top { get; init; }
}

/// <summary>
/// Encodes/decodes the adapter-owned mail delta token: unpadded base64url of a versioned
/// JSON payload. Decoding is tolerant: anything undecodable, from another version, or
/// for another folder is <see cref="ErrorKind.SyncStateGone"/> (HTTP 410), which makes
/// the client resync instead of failing.
/// </summary>
internal static class JmapDeltaTokenCodec
{
    public const int CurrentVersion = 1;

    public static DeltaToken Encode(JmapDeltaCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentException.ThrowIfNullOrEmpty(cursor.MailboxId);
        if (cursor.Phase == DeltaPhase.Initial && cursor.Position is not >= 0)
        {
            throw new ArgumentException("An initial-phase cursor needs a non-negative position.", nameof(cursor));
        }

        if (cursor.Phase == DeltaPhase.Changes && string.IsNullOrEmpty(cursor.State))
        {
            throw new ArgumentException("A changes-phase cursor needs a state.", nameof(cursor));
        }

        var payload = new JmapDeltaCursorPayload
        {
            Version = CurrentVersion,
            MailboxId = cursor.MailboxId,
            Phase = cursor.Phase == DeltaPhase.Initial ? "i" : "c",
            Position = cursor.Phase == DeltaPhase.Initial ? cursor.Position : null,
            State = cursor.State,
            Select = cursor.Select,
            Top = cursor.Top,
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JmapJsonContext.Default.JmapDeltaCursorPayload);
        return new DeltaToken(Base64Url.EncodeToString(bytes));
    }

    public static Result<JmapDeltaCursor> Decode(DeltaToken token)
    {
        JmapDeltaCursorPayload? payload;
        try
        {
            if (string.IsNullOrEmpty(token.Value))
            {
                return Gone();
            }

            payload = JsonSerializer.Deserialize(Base64Url.DecodeFromChars(token.Value), JmapJsonContext.Default.JmapDeltaCursorPayload);
        }
        catch (FormatException)
        {
            return Gone();
        }
        catch (JsonException)
        {
            return Gone();
        }

        if (payload is null || payload.Version != CurrentVersion || string.IsNullOrEmpty(payload.MailboxId))
        {
            return Gone();
        }

        return payload.Phase switch
        {
            "i" when payload.Position is >= 0 =>
                new JmapDeltaCursor(payload.MailboxId, DeltaPhase.Initial, payload.Position, payload.State) { Select = payload.Select, Top = payload.Top },
            "c" when !string.IsNullOrEmpty(payload.State) =>
                new JmapDeltaCursor(payload.MailboxId, DeltaPhase.Changes, null, payload.State) { Select = payload.Select, Top = payload.Top },
            _ => Gone(),
        };
    }

    /// <summary>Decodes and checks the token belongs to <paramref name="expectedMailboxId"/>.</summary>
    public static Result<JmapDeltaCursor> Decode(DeltaToken token, string expectedMailboxId) =>
        Decode(token).Bind(cursor => string.Equals(cursor.MailboxId, expectedMailboxId, StringComparison.Ordinal)
            ? Result<JmapDeltaCursor>.Success(cursor)
            : Result<JmapDeltaCursor>.Failure(Error.SyncStateGone("The delta token belongs to another folder.")));

    private static Error Gone() => Error.SyncStateGone("The delta token is invalid or has expired; resync the folder.");
}
