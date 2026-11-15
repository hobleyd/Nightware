using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nightware.Adapters.Dav.Core;
using Nightware.Application.Tasks;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Tasks.Attachments;

/// <summary>
/// What an upload session must remember until its PUT: the target task and the file it was opened
/// for. Carried as the session token (<c>UploadSession.Token</c>), unpadded base64url JSON, so the
/// façade keeps no state.
/// </summary>
/// <remarks>
/// The token is not secret and not signed here. What keeps a hand-built one out is the Api: the PUT
/// route accepts only a self-authorising upload URL, whose sealed ticket is bound to this token and to
/// the route's mailbox, list and task, and names the file's name and size. Behind that, every field is
/// re-checked on use — the list against the mailbox, the task against the list, the name and size
/// against <see cref="TaskAttachmentLimits"/>, the body against <see cref="Size"/> — and the caller's
/// own credentials decide what may be written.
/// </remarks>
internal sealed record TaskUploadToken(
    [property: JsonPropertyName("m")] string Mailbox,
    [property: JsonPropertyName("l")] string ListHref,
    [property: JsonPropertyName("t")] string TaskHref,
    [property: JsonPropertyName("n")] string Name,
    [property: JsonPropertyName("c")] string ContentType,
    [property: JsonPropertyName("s")] long Size,
    [property: JsonPropertyName("e")] long ExpiresAtUnixSeconds)
{
    private const int MaxEncodedLength = 4096;

    [JsonPropertyName("v")]
    public int Version { get; init; } = 1;

    [JsonIgnore]
    public DateTimeOffset ExpiresAt => DateTimeOffset.FromUnixTimeSeconds(ExpiresAtUnixSeconds);

    public string Encode() => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(this));

    /// <summary>The token, or <see cref="ErrorKind.NotFound"/> for anything malformed, of another version, beyond the attachment limits, or expired.</summary>
    public static Result<TaskUploadToken> Decode(string? token, DateTimeOffset now)
    {
        var missing = Error.NotFound("The upload session was not found or has expired.");
        if (string.IsNullOrEmpty(token) || token.Length > MaxEncodedLength || !Base64Url.IsValid(token))
        {
            return missing;
        }

        try
        {
            var decoded = JsonSerializer.Deserialize<TaskUploadToken>(Base64Url.DecodeFromChars(token));
            return decoded is { Version: 1 } valid
                   && TaskAttachmentLimits.Validate(valid.Name, valid.Size) is null
                   && !string.IsNullOrEmpty(valid.Mailbox)
                   && DavHrefRules.IsSafeRelative(valid.ListHref) && DavHrefRules.IsSafeRelative(valid.TaskHref)
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
