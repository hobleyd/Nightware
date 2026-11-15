using Nightware.Domain.Kernel;
using Nightware.Domain.Tasks;

namespace Nightware.Application.Tasks;

/// <summary>
/// Receives the bytes of an upload session (the <c>PUT {uploadUrl}</c>). NightMail sends the whole
/// file in one request, which is the only shape accepted: a session is completed by exactly one PUT
/// of bytes <c>0…size-1</c>.
/// </summary>
public sealed class UploadTaskAttachmentContent(ITaskAttachmentStore attachments)
{
    /// <summary>
    /// The <c>rangeEnd</c> passed to <see cref="ITaskAttachmentStore.PutChunkAsync"/> when the request
    /// carried no <c>Content-Range</c>: the body is the whole file, and the store checks its length
    /// against the size the session was opened for.
    /// </summary>
    public const long ToEnd = -1;

    /// <param name="sessionToken">The store's session token, as it appears in the upload URL.</param>
    /// <param name="range">The request's <c>Content-Range</c>, or null when it sent none.</param>
    public async Task<Result<TaskAttachment>> ExecuteAsync(string sessionToken, ContentRange? range, Stream content, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionToken);
        ArgumentNullException.ThrowIfNull(content);
        if (range is not null && (range.First != 0 || range.Total <= 0 || range.Last != range.Total - 1))
        {
            return Error.Validation("This upload session takes the whole file in one request: Content-Range must be bytes 0-{size-1}/{size}.", "Content-Range");
        }

        // Only the token is known here; the store reads the size and expiry it was opened with from it.
        var session = new UploadSession(sessionToken, DateTimeOffset.MaxValue, range?.Total ?? 0);
        var progress = await attachments.PutChunkAsync(session, 0, range?.Last ?? ToEnd, content, cancellationToken).ConfigureAwait(false);
        if (!progress.IsSuccess)
        {
            return progress.Error;
        }

        return progress.Value.Completed is { } attachment
            ? attachment
            : new Error(ErrorKind.Unknown, "generalException", "The upload ended without creating the attachment.");
    }
}
