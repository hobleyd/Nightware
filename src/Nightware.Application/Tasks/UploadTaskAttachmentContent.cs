using Nightware.Domain.Kernel;
using Nightware.Domain.Tasks;

namespace Nightware.Application.Tasks;

/// <summary>The file an upload session was opened for, as the caller's sealed upload URL vouches.</summary>
public sealed record ExpectedUpload(string Name, long Size);

/// <summary>
/// Receives the bytes of an upload session (the <c>PUT {uploadUrl}</c>). NightMail sends the whole
/// file in one request, which is the only shape accepted: a session is completed by exactly one PUT
/// of bytes <c>0…size-1</c>.
/// </summary>
/// <remarks>
/// The limits <see cref="BeginTaskAttachmentUpload"/> applied are applied again here, to the file the
/// upload URL names: the session token is the store's and unsigned, so the PUT does not take its word.
/// </remarks>
public sealed class UploadTaskAttachmentContent(ITaskAttachmentStore attachments)
{
    /// <summary>
    /// The <c>rangeEnd</c> passed to <see cref="ITaskAttachmentStore.PutChunkAsync"/> when the request
    /// carried no <c>Content-Range</c>: the body is the whole file, and the store checks its length
    /// against the size the session was opened for.
    /// </summary>
    public const long ToEnd = -1;

    /// <param name="sessionToken">The store's session token, as it appears in the upload URL.</param>
    /// <param name="expected">The file the upload URL was minted for.</param>
    /// <param name="range">The request's <c>Content-Range</c>, or null when it sent none.</param>
    public async Task<Result<TaskAttachment>> ExecuteAsync(
        string sessionToken, ExpectedUpload expected, ContentRange? range, Stream content, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionToken);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(content);
        if (TaskAttachmentLimits.Validate(expected.Name, expected.Size) is { } invalid)
        {
            return invalid;
        }

        if (range is not null && (range.First != 0 || range.Total != expected.Size || range.Last != range.Total - 1))
        {
            return Error.Validation($"This upload session takes the whole file in one request: Content-Range must be bytes 0-{expected.Size - 1}/{expected.Size}.", "Content-Range");
        }

        var session = new UploadSession(sessionToken, DateTimeOffset.MaxValue, expected.Size);
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
