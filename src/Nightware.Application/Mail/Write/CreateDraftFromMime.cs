using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail.Write;

/// <summary>Graph <c>POST messages</c> with a MIME body (<c>text/plain</c>, base64): the message saved as a draft (201).</summary>
public sealed class CreateDraftFromMime(IDraftStore drafts, IMailMessageReader reader)
{
    /// <summary>Graph's limit on a MIME payload (user-post-messages: larger is 413).</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    public async Task<Result<MailMessage>> ExecuteAsync(MailboxRef mailbox, ReadOnlyMemory<byte> mime, CancellationToken cancellationToken)
    {
        if (MimeLimit.Check(mime) is { } tooLarge)
        {
            return tooLarge;
        }

        var created = await drafts.CreateFromMimeAsync(mailbox, mime, cancellationToken).ConfigureAwait(false);
        if (!created.IsSuccess)
        {
            return created.Error;
        }

        // As WrittenMessage: the draft exists, so a failed re-read still answers with its id rather than an error.
        var read = await reader.GetAsync(mailbox, created.Value, MessageFetch.Default, cancellationToken).ConfigureAwait(false);
        return read.IsSuccess ? read : new MailMessage(created.Value) { IsDraft = true, IsRead = true };
    }
}

/// <summary>The size rule both MIME writes share.</summary>
internal static class MimeLimit
{
    public static Error? Check(ReadOnlyMemory<byte> mime) =>
        mime.Length > CreateDraftFromMime.MaxBytes
            ? new Error(ErrorKind.PayloadTooLarge, "requestEntityTooLarge", "The MIME content is larger than 4 MB.")
            : mime.IsEmpty ? Error.Validation("The MIME content is empty.") : null;
}
