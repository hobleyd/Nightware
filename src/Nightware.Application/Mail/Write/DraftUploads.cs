using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail.Write;

/// <summary>
/// Where the chunks of a draft attachment upload wait until the last one arrives (ADR 0006 D5). The use cases
/// own the rules; the store keeps the bytes and the session, which lives with the draft.
/// </summary>
public interface IDraftUploadStaging
{
    /// <summary>Opens a session for <paramref name="file"/> on the draft.</summary>
    Task<Result<DraftUploadState>> BeginAsync(MailboxRef mailbox, MessageId draftId, DraftUploadFile file, CancellationToken cancellationToken);

    /// <summary>
    /// Adds <paramref name="length"/> bytes starting at <paramref name="first"/>, which must be the next byte the
    /// session wants, of a file of <paramref name="total"/> bytes, which must be the session's. Nothing is stored otherwise.
    /// </summary>
    Task<Result<DraftUploadState>> AppendAsync(
        MailboxRef mailbox, MessageId draftId, string sessionId, long first, long length, long total, Stream chunk, CancellationToken cancellationToken);

    /// <summary>The whole file of a complete session.</summary>
    Task<Result<ReadOnlyMemory<byte>>> ReadAsync(MailboxRef mailbox, MessageId draftId, string sessionId, CancellationToken cancellationToken);
}

/// <summary>Limits on a draft attachment upload.</summary>
public static class DraftUploadLimits
{
    /// <summary>Stalwart's default JMAP <c>maxSizeUpload</c>: the largest blob, and so message, it accepts.</summary>
    public const long MaxBytes = 50_000_000;

    public static Error? Validate(DraftUploadFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (string.IsNullOrWhiteSpace(file.Name))
        {
            return Error.Validation("An attachment needs a name.", "AttachmentItem.name");
        }

        if (file.Size <= 0)
        {
            return Error.Validation("An attachment needs a positive size.", "AttachmentItem.size");
        }

        return file.Size > MaxBytes
            ? new Error(ErrorKind.PayloadTooLarge, "requestEntityTooLarge", $"An attachment can be at most {MaxBytes} bytes.")
            : null;
    }
}

/// <summary>Graph <c>POST messages/{id}/attachments/createUploadSession</c> on a draft.</summary>
public sealed class BeginDraftAttachmentUpload(IDraftUploadStaging staging)
{
    public async Task<Result<DraftUploadState>> ExecuteAsync(MailboxRef mailbox, MessageId draftId, DraftUploadFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (DraftUploadLimits.Validate(file) is { } invalid)
        {
            return invalid;
        }

        var normalised = file with
        {
            Name = file.Name.Trim(),
            ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
        };
        return await staging.BeginAsync(mailbox, draftId, normalised, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Graph <c>PUT {uploadUrl}</c>: one chunk, named by its <c>Content-Range</c>. The last chunk turns the file into
/// an attachment of the draft through <see cref="IDraftStore"/>, as an attachment sent inline would be.
/// </summary>
public sealed class UploadDraftAttachmentChunk(IDraftUploadStaging staging, IDraftStore drafts)
{
    public async Task<Result<DraftUploadProgress>> ExecuteAsync(
        MailboxRef mailbox, MessageId draftId, string sessionId, ContentRangeHeader range, Stream chunk, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(chunk);
        if (range.First < 0 || range.Last < range.First || range.Total <= range.Last)
        {
            return Error.Validation("Content-Range must be 'bytes first-last/total' with first ≤ last < total.", "Content-Range");
        }

        var state = await staging.AppendAsync(mailbox, draftId, sessionId, range.First, range.Last - range.First + 1, range.Total, chunk, cancellationToken).ConfigureAwait(false);
        if (!state.IsSuccess)
        {
            return state.Error;
        }

        if (!state.Value.IsComplete)
        {
            return new DraftUploadProgress(state.Value.Received, null);
        }

        var content = await staging.ReadAsync(mailbox, draftId, sessionId, cancellationToken).ConfigureAwait(false);
        if (!content.IsSuccess)
        {
            return content.Error;
        }

        var file = state.Value.File;
        var attached = await drafts.AddAttachmentAsync(mailbox, draftId, new NewAttachment(file.Name, file.ContentType, content.Value)
        {
            IsInline = file.IsInline,
            ContentId = file.ContentId,
        }, cancellationToken).ConfigureAwait(false);
        return attached.IsSuccess ? new DraftUploadProgress(file.Size, attached.Value) : attached.Error;
    }
}

/// <summary>A request's <c>Content-Range: bytes first-last/total</c>.</summary>
public sealed record ContentRangeHeader(long First, long Last, long Total);
