using System.Globalization;
using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Tasks.Attachments;
using Nightware.Application.Calendar.Calendars;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Calendar;

/// <summary>
/// Large event attachments in chunks (phase 19) over Nextcloud's chunked upload (v1): the chunks are staged in
/// <c>uploads/{uid}/nw-{uuid}/</c>, each named by its zero-padded first byte; the last one is followed by a <c>MOVE</c>
/// of the folder's virtual <c>.file</c> (the chunks joined, in name order) into the owner's Files, where
/// <see cref="DavEventAttachmentStore.LinkAsync"/> gives the event its <c>ATTACH</c>.
/// </summary>
/// <remarks>
/// <para>Verified on Nextcloud 35 (phase 19 Step 0): MKCOL, the chunk PUTs and the MOVE work with the caller's bearer;
/// the MOVE removes the staging folder. Chunking v1 has no minimum chunk size, so Graph clients' 320 KiB multiples fit
/// (v2 wants 5 MB). What has arrived is read back from the folder's listing, so the façade keeps no state.</para>
/// <para>As with a small attachment, the file goes to the event owner's Files; for a delegate that works only where
/// Nextcloud lets them write there (docs/known-issues.md). Staging folders of sessions never finished are left for
/// Nextcloud's own cleanup of stale uploads.</para>
/// </remarks>
internal sealed class DavEventAttachmentUploads(
    DavEventResources resources, DavCalendarScopes scopes, TaskAttachmentFiles files, DavEventAttachmentStore attachments, IDavClient dav, TimeProvider time)
    : IEventAttachmentUploads
{
    /// <summary>A session's life, as a mail upload's: the upload URL may end it sooner.</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(1);

    private const string FileOfChunks = ".file";

    public async Task<Result<EventUploadState>> BeginAsync(MailboxRef mailbox, EventId eventId, EventUploadFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        // Fail now, not after the client has sent megabytes, when the event does not exist.
        var stored = await resources.LoadAsync(mailbox, eventId, cancellationToken).ConfigureAwait(false);
        if (!stored.IsSuccess)
        {
            return stored.Error;
        }

        var scope = await scopes.ResolveAsync(mailbox, cancellationToken).ConfigureAwait(false);
        var home = scope.Bind(s => files.HomeOf(s.Owner));
        if (!home.IsSuccess)
        {
            return home.Error;
        }

        var upload = $"uploads/{home.Value["files/".Length..]}nw-{Guid.NewGuid():N}/";
        var made = await dav.MkColAsync(upload, null, cancellationToken).ConfigureAwait(false);
        if (!made.IsSuccess)
        {
            return made.Error;
        }

        var expires = time.GetUtcNow() + SessionLifetime;
        var token = new EventUploadToken(mailbox.Address, eventId.Value, file.Name, file.ContentType, file.Size, upload, home.Value, expires.ToUnixTimeSeconds());
        return new EventUploadState(token.Encode(), file, 0, token.ExpiresAt);
    }

    public async Task<Result<EventUploadProgress>> AppendAsync(
        MailboxRef mailbox, EventId eventId, string sessionId, long first, long length, long total, Stream chunk, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var decoded = EventUploadToken.Decode(sessionId, time.GetUtcNow());
        if (!decoded.IsSuccess)
        {
            return decoded.Error;
        }

        var token = decoded.Value;
        if (!string.Equals(token.Mailbox, mailbox.Address, StringComparison.OrdinalIgnoreCase) || token.EventId != eventId.Value)
        {
            return Error.NotFound("The upload session was not found or has expired.");
        }

        if (total != token.Size)
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
        return next < token.Size
            ? new EventUploadProgress(next, null)
            : await CompleteAsync(mailbox, eventId, token, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The bytes staged so far: the chunks' lengths (they are contiguous, since only the next one is accepted).</summary>
    private async Task<Result<long>> ReceivedAsync(string uploadHref, CancellationToken cancellationToken)
    {
        var listed = await dav.PropFindAsync(uploadHref, DavDepth.One, [DavNames.GetContentLength], cancellationToken).ConfigureAwait(false);
        if (!listed.IsSuccess)
        {
            return listed.Error.Kind == ErrorKind.NotFound ? Error.NotFound("The upload session was not found or has expired.") : listed.Error;
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

            if (long.TryParse(response.Find(DavNames.GetContentLength)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var size))
            {
                sum += size;
            }
        }

        return sum;
    }

    private async Task<Result<EventUploadProgress>> CompleteAsync(MailboxRef mailbox, EventId eventId, EventUploadToken token, CancellationToken cancellationToken)
    {
        // Re-check the event exactly as a fresh request would before the file lands.
        var stored = await resources.LoadAsync(mailbox, eventId, cancellationToken).ConfigureAwait(false);
        if (!stored.IsSuccess)
        {
            return stored.Error;
        }

        var folder = await files.NewFolderAsync(token.HomeHref, DavEventAttachmentStore.Folder, cancellationToken).ConfigureAwait(false);
        if (!folder.IsSuccess)
        {
            return folder.Error;
        }

        var href = folder.Value + Uri.EscapeDataString(TaskAttachmentFiles.SafeName(token.Name));
        var moved = await dav.MoveAsync(token.UploadHref + FileOfChunks, href, overwrite: false, cancellationToken).ConfigureAwait(false);
        if (!moved.IsSuccess)
        {
            await files.DeleteFolderOfAsync(href, cancellationToken).ConfigureAwait(false);
            return moved.Error;
        }

        var linked = await attachments.LinkAsync(stored.Value, href, token.Name, token.ContentType, token.Size, cancellationToken).ConfigureAwait(false);
        return linked.Map(a => new EventUploadProgress(token.Size, a));
    }
}
