using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Tasks.Attachments;
using Nightware.Application.Files.Drives;
using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Files.Drive;

/// <summary>
/// <see cref="IDriveUploads"/> over Nextcloud's chunked upload v1, as event attachments (phase 19): chunks are staged in
/// <c>uploads/{uid}/nw-{uuid}/</c>, each named by its zero-padded first byte, and the last is followed by a <c>MOVE</c> of the
/// folder's virtual <c>.file</c> to the target. Progress is read back from the staging folder, so the token is all the state.
/// </summary>
internal sealed class NextcloudDriveUploads(IDavClient dav, DriveHomeLocator homes, DriveItemLocator locator, TimeProvider time) : IDriveUploads
{
    /// <summary>How long a session lives (Graph keeps one for days; an upload URL here works for this long).</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(24);

    private const string FileOfChunks = ".file";

    public async Task<Result<DriveUploadState>> BeginAsync(DriveUploadFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        var home = await locator.HomeAsync(file.Parent, cancellationToken).ConfigureAwait(false);
        if (!home.IsSuccess)
        {
            return home.Error;
        }

        var parent = await locator.FolderAsync(home.Value, file.Parent, cancellationToken).ConfigureAwait(false);
        if (!parent.IsSuccess)
        {
            return parent.Error;
        }

        // Fail now, not after the client has sent gigabytes, when the name is taken and the client asked to fail.
        if (file.Conflict == ConflictBehavior.Fail)
        {
            var taken = await locator.NamesInAsync(home.Value, parent.Value.Href, cancellationToken).ConfigureAwait(false);
            if (!taken.IsSuccess)
            {
                return taken.Error;
            }

            if (taken.Value.Contains(file.Name))
            {
                return new Error(ErrorKind.Conflict, DriveErrors.NameAlreadyExistsCode, "An item with that name already exists.");
            }
        }

        var upload = $"{home.Value.UploadsHref}nw-{Guid.NewGuid():N}/";
        var made = await dav.MkColAsync(upload, null, cancellationToken).ConfigureAwait(false);
        if (!made.IsSuccess)
        {
            return made.Error;
        }

        var expires = time.GetUtcNow() + SessionLifetime;
        var token = new DriveUploadToken(home.Value.Uid, upload, parent.Value.Href, file.Name, file.Size, file.Conflict, expires.ToUnixTimeSeconds());
        return new DriveUploadState(token.Encode(), token.ExpiresAt, 0);
    }

    public async Task<Result<DriveUploadProgress>> AppendAsync(string sessionId, long first, long length, long total, Stream chunk, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var opened = await OpenAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return opened.Error;
        }

        var (home, token) = opened.Value;
        if (token.Size > 0 && total != token.Size)
        {
            return Error.Validation($"The session was opened for {token.Size} bytes, not {total}.", "Content-Range");
        }

        var received = await ReceivedAsync(token.UploadHref, cancellationToken).ConfigureAwait(false);
        if (!received.IsSuccess)
        {
            return received.Error;
        }

        if (first != received.Value)
        {
            return Error.Validation($"The next byte expected is {received.Value.ToString(CultureInfo.InvariantCulture)}.", "Content-Range");
        }

        var counted = new CountingReadStream(chunk, length);
        var name = first.ToString("D15", CultureInfo.InvariantCulture);
        var put = await dav.PutAsync(token.UploadHref + name, counted, "application/octet-stream", DavPrecondition.CreateOnly, cancellationToken).ConfigureAwait(false);
        if (!put.IsSuccess)
        {
            return put.Error;
        }

        if (counted.Exceeded || counted.BytesRead != length)
        {
            // Nothing that disagrees with its Content-Range may stay, or the joined file would be wrong.
            await dav.DeleteAsync(token.UploadHref + name, DavPrecondition.None, cancellationToken).ConfigureAwait(false);
            return Error.Validation($"The chunk holds {counted.BytesRead} bytes, but its Content-Range says {length}.", "Content-Range");
        }

        var next = first + length;
        return next < total
            ? new DriveUploadProgress(next, null)
            : await CompleteAsync(home, token, total, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<DriveUploadState>> GetAsync(string sessionId, CancellationToken cancellationToken)
    {
        var opened = await OpenAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return opened.Error;
        }

        var received = await ReceivedAsync(opened.Value.Token.UploadHref, cancellationToken).ConfigureAwait(false);
        return received.IsSuccess ? new DriveUploadState(sessionId, opened.Value.Token.ExpiresAt, received.Value) : received.Error;
    }

    public async Task<Result> CancelAsync(string sessionId, CancellationToken cancellationToken)
    {
        var opened = await OpenAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return opened.Error;
        }

        var deleted = await dav.DeleteAsync(opened.Value.Token.UploadHref, DavPrecondition.None, cancellationToken).ConfigureAwait(false);
        return deleted.IsSuccess || deleted.Error.Kind == ErrorKind.NotFound ? Result.Success() : deleted;
    }

    /// <summary>The token, provided it is valid, unexpired, and the caller's own.</summary>
    private async Task<Result<(DriveHome Home, DriveUploadToken Token)>> OpenAsync(string sessionId, CancellationToken cancellationToken)
    {
        var token = DriveUploadToken.Decode(sessionId, time.GetUtcNow());
        if (token is null)
        {
            return Missing();
        }

        var home = await homes.CurrentAsync(cancellationToken).ConfigureAwait(false);
        if (!home.IsSuccess)
        {
            return home.Error;
        }

        return token.Uid == home.Value.Uid
               && token.UploadHref.StartsWith(home.Value.UploadsHref, StringComparison.Ordinal)
               && token.ParentHref.StartsWith(home.Value.Href, StringComparison.Ordinal)
            ? (home.Value, token)
            : Missing();
    }

    /// <summary>The bytes staged so far: the chunks' lengths (they are contiguous, since only the next one is accepted).</summary>
    private async Task<Result<long>> ReceivedAsync(string uploadHref, CancellationToken cancellationToken)
    {
        var listed = await dav.PropFindAsync(uploadHref, DavDepth.One, [DavNames.GetContentLength], cancellationToken).ConfigureAwait(false);
        if (!listed.IsSuccess)
        {
            return listed.Error.Kind == ErrorKind.NotFound ? Missing() : listed.Error;
        }

        long sum = 0;
        foreach (var response in listed.Value.Responses)
        {
            var leaf = response.Href.TrimEnd('/');
            leaf = leaf[(leaf.LastIndexOf('/') + 1)..];
            if (response.Href.EndsWith('/') || leaf.StartsWith('.') || !response.IsFound)
            {
                continue; // the folder itself and Nextcloud's virtual .file
            }

            sum += DriveItemMapping.Long(response.Find(DavNames.GetContentLength)?.Value) ?? 0;
        }

        return sum;
    }

    private async Task<Result<DriveUploadProgress>> CompleteAsync(DriveHome home, DriveUploadToken token, long total, CancellationToken cancellationToken)
    {
        var name = token.Name;
        if (token.Conflict != ConflictBehavior.Replace)
        {
            var taken = await locator.NamesInAsync(home, token.ParentHref, cancellationToken).ConfigureAwait(false);
            if (!taken.IsSuccess)
            {
                return taken.Error;
            }

            if (taken.Value.Contains(name))
            {
                if (token.Conflict == ConflictBehavior.Fail)
                {
                    return new Error(ErrorKind.Conflict, DriveErrors.NameAlreadyExistsCode, "An item with that name already exists.");
                }

                name = DriveItemLocator.FreeName(name, taken.Value);
            }
        }

        var href = DriveItemLocator.Child(token.ParentHref, name);
        var moved = await dav.MoveAsync(token.UploadHref + FileOfChunks, href, overwrite: token.Conflict == ConflictBehavior.Replace, cancellationToken).ConfigureAwait(false);
        if (!moved.IsSuccess)
        {
            return moved.Error;
        }

        var located = await locator.PropFindAsync(home, href, cancellationToken).ConfigureAwait(false);
        if (!located.IsSuccess)
        {
            return located.Error;
        }

        var item = await locator.ToItemAsync(home, located.Value, cancellationToken).ConfigureAwait(false);
        return item.IsSuccess ? new DriveUploadProgress(total, item.Value) : item.Error;
    }

    private static Error Missing() => Error.NotFound("The upload session was not found or has expired.");
}

/// <summary>
/// What a drive upload session remembers (unpadded base64url JSON, so the façade keeps no state). Not signed here: the
/// upload URL that carries it is sealed and bound to it, and every field is re-checked against the caller's own home.
/// </summary>
internal sealed record DriveUploadToken(
    [property: JsonPropertyName("u")] string Uid,
    [property: JsonPropertyName("s")] string UploadHref,
    [property: JsonPropertyName("p")] string ParentHref,
    [property: JsonPropertyName("n")] string Name,
    [property: JsonPropertyName("l")] long Size,
    [property: JsonPropertyName("c")] ConflictBehavior Conflict,
    [property: JsonPropertyName("e")] long ExpiresAtUnixSeconds)
{
    private const int MaxEncodedLength = 4096;

    [JsonPropertyName("v")]
    public int Version { get; init; } = 1;

    [JsonIgnore]
    public DateTimeOffset ExpiresAt => DateTimeOffset.FromUnixTimeSeconds(ExpiresAtUnixSeconds);

    public string Encode() => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(this));

    public static DriveUploadToken? Decode(string? token, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(token) || token.Length > MaxEncodedLength || !Base64Url.IsValid(token))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DriveUploadToken>(Base64Url.DecodeFromChars(token)) is { Version: 1 } valid
                   && DriveItemRef.IsValidName(valid.Name)
                   && valid.Size is >= 0 and <= DriveLimits.UploadSessionMaxBytes
                   && Enum.IsDefined(valid.Conflict)
                   && valid.UploadHref.StartsWith("uploads/", StringComparison.Ordinal) && DavHrefRules.IsSafeRelative(valid.UploadHref)
                   && valid.ParentHref.StartsWith("files/", StringComparison.Ordinal) && DavHrefRules.IsSafeRelative(valid.ParentHref)
                   && valid.ExpiresAt > now
                ? valid
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
