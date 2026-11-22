using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nightware.Adapters.Dav.Core;
using Nightware.Application.Calendar.Calendars;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Calendar;

/// <summary>
/// What an event attachment upload session remembers: the event, the file, and the Nextcloud upload folder its chunks
/// are staged in. The session token itself (unpadded base64url JSON), so the façade keeps no state.
/// </summary>
/// <remarks>
/// Not secret and not signed here, as <c>TaskUploadToken</c>: the PUT route takes only a self-authorising upload URL whose
/// sealed ticket is bound to this token and to the route's mailbox and event. Behind that every field is re-checked
/// (the event against the mailbox, the name and size against <see cref="EventUploadLimits"/>, the folders as safe
/// relative hrefs), and the caller's own credentials decide what may be written.
/// </remarks>
internal sealed record EventUploadToken(
    [property: JsonPropertyName("m")] string Mailbox,
    [property: JsonPropertyName("i")] string EventId,
    [property: JsonPropertyName("n")] string Name,
    [property: JsonPropertyName("c")] string ContentType,
    [property: JsonPropertyName("s")] long Size,
    [property: JsonPropertyName("u")] string UploadHref,
    [property: JsonPropertyName("h")] string HomeHref,
    [property: JsonPropertyName("e")] long ExpiresAtUnixSeconds)
{
    private const int MaxEncodedLength = 4096;

    [JsonPropertyName("v")]
    public int Version { get; init; } = 1;

    [JsonIgnore]
    public DateTimeOffset ExpiresAt => DateTimeOffset.FromUnixTimeSeconds(ExpiresAtUnixSeconds);

    public string Encode() => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(this));

    /// <summary>The token, or <see cref="ErrorKind.NotFound"/> for anything malformed, of another version, out of limits, or expired.</summary>
    public static Result<EventUploadToken> Decode(string? token, DateTimeOffset now)
    {
        var missing = Error.NotFound("The upload session was not found or has expired.");
        if (string.IsNullOrEmpty(token) || token.Length > MaxEncodedLength || !Base64Url.IsValid(token))
        {
            return missing;
        }

        try
        {
            var decoded = JsonSerializer.Deserialize<EventUploadToken>(Base64Url.DecodeFromChars(token));
            return decoded is { Version: 1 } valid
                   && EventUploadLimits.Validate(valid.Name, valid.Size) is null
                   && !string.IsNullOrEmpty(valid.Mailbox) && !string.IsNullOrEmpty(valid.EventId)
                   && valid.UploadHref.StartsWith("uploads/", StringComparison.Ordinal) && DavHrefRules.IsSafeRelative(valid.UploadHref)
                   && valid.HomeHref.StartsWith("files/", StringComparison.Ordinal) && DavHrefRules.IsSafeRelative(valid.HomeHref)
                   && !string.IsNullOrWhiteSpace(valid.ContentType)
                   && valid.ExpiresAt > now
                ? valid
                : missing;
        }
        catch (JsonException)
        {
            return missing;
        }
    }
}
