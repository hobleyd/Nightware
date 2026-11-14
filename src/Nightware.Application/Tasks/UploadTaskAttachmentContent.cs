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
    /// <param name="range">The request's <c>Content-Range</c> (<c>first</c>, <c>last</c>, <c>total</c>), or null when it sent none (taken as the whole file).</param>
    public async Task<Result<TaskAttachment>> ExecuteAsync(UploadSession session, ContentRange? range, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(content);
        var whole = new ContentRange(0, session.TotalSize - 1, session.TotalSize);
        if (range is not null && range != whole)
        {
            return Error.Validation(
                $"This upload session takes the whole file in one request: Content-Range must be bytes 0-{session.TotalSize - 1}/{session.TotalSize}.",
                "Content-Range");
        }

        var progress = await attachments.PutChunkAsync(session, whole.First, whole.Last, content, cancellationToken).ConfigureAwait(false);
        if (!progress.IsSuccess)
        {
            return progress.Error;
        }

        return progress.Value.Completed is { } attachment
            ? attachment
            : new Error(ErrorKind.Unknown, "generalException", "The upload ended without creating the attachment.");
    }
}
