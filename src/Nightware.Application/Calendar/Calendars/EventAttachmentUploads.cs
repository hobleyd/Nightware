using Nightware.Application.Mail.Write;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Calendar.Calendars;

/// <summary>The file an event attachment upload session is opened for (Graph's <c>AttachmentItem</c>).</summary>
public sealed record EventUploadFile(string Name, string ContentType, long Size);

/// <summary>A session's state: what it is for, and how many bytes have arrived.</summary>
public sealed record EventUploadState(string SessionId, EventUploadFile File, long Received, DateTimeOffset ExpiresAt);

/// <summary>After a chunk: the next byte wanted, or the attachment the last chunk completed.</summary>
public sealed record EventUploadProgress(long NextExpected, EventAttachment? Completed);

/// <summary>
/// Large event attachments in chunks (phase 19). The store stages the chunks and, when the last arrives, makes the file
/// an attachment of the event exactly as <see cref="IEventAttachmentStore.AddAsync"/> would.
/// </summary>
public interface IEventAttachmentUploads
{
    Task<Result<EventUploadState>> BeginAsync(MailboxRef mailbox, EventId eventId, EventUploadFile file, CancellationToken cancellationToken);

    /// <summary>
    /// Stores <paramref name="length"/> bytes starting at <paramref name="first"/>, which must be the next byte the session
    /// wants, of a file of <paramref name="total"/> bytes, which must be the session's.
    /// </summary>
    Task<Result<EventUploadProgress>> AppendAsync(
        MailboxRef mailbox, EventId eventId, string sessionId, long first, long length, long total, Stream chunk, CancellationToken cancellationToken);
}

/// <summary>Limits on an event attachment upload.</summary>
public static class EventUploadLimits
{
    /// <summary>Graph's upper bound for an upload session (attachment-createuploadsession: 3 MB to 150 MB).</summary>
    public const long MaxBytes = 150L * 1024 * 1024;

    /// <summary>Graph's bound on one chunk ("up to 4 MB each time").</summary>
    public const long MaxChunkBytes = 4L * 1024 * 1024;

    public static Error? Validate(string? name, long size)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Error.Validation("An attachment needs a name.", "AttachmentItem.name");
        }

        if (size <= 0)
        {
            return Error.Validation("An attachment needs a positive size.", "AttachmentItem.size");
        }

        return size > MaxBytes
            ? new Error(ErrorKind.PayloadTooLarge, "requestEntityTooLarge", $"An attachment can be at most {MaxBytes} bytes.")
            : null;
    }
}

/// <summary>Graph <c>POST events/{id}/attachments/createUploadSession</c>.</summary>
public sealed class BeginEventAttachmentUpload(IEventAttachmentUploads uploads)
{
    public async Task<Result<EventUploadState>> ExecuteAsync(MailboxRef mailbox, EventId eventId, string? name, string? contentType, long size, CancellationToken cancellationToken)
    {
        if (EventUploadLimits.Validate(name, size) is { } invalid)
        {
            return invalid;
        }

        var file = new EventUploadFile(name!.Trim(), string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType.Trim(), size);
        return await uploads.BeginAsync(mailbox, eventId, file, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Graph <c>PUT {uploadUrl}</c> for an event attachment: one chunk, named by its <c>Content-Range</c>.</summary>
public sealed class UploadEventAttachmentChunk(IEventAttachmentUploads uploads)
{
    public async Task<Result<EventUploadProgress>> ExecuteAsync(
        MailboxRef mailbox, EventId eventId, string sessionId, ContentRangeHeader range, Stream chunk, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(chunk);
        if (range.First < 0 || range.Last < range.First || range.Total <= range.Last)
        {
            return Error.Validation("Content-Range must be 'bytes first-last/total' with first ≤ last < total.", "Content-Range");
        }

        var length = range.Last - range.First + 1;
        if (length > EventUploadLimits.MaxChunkBytes)
        {
            return new Error(ErrorKind.PayloadTooLarge, "requestEntityTooLarge", $"A chunk can be at most {EventUploadLimits.MaxChunkBytes} bytes.");
        }

        return await uploads.AppendAsync(mailbox, eventId, sessionId, range.First, length, range.Total, chunk, cancellationToken).ConfigureAwait(false);
    }
}
