using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail.Write;

/// <summary>Builds the response body of a write that created a message (ADR 0001 §35: writes return ids).</summary>
internal static class WrittenMessage
{
    /// <summary>
    /// The created message, re-read through the one Email → <see cref="MailMessage"/> mapping. When the
    /// re-read fails the message still exists, and an error would make the client create it again; it
    /// reads only <c>id</c> from these responses, so it gets the message as written instead.
    /// </summary>
    public static async Task<Result<MailMessage>> ReadAsync(IMailMessageReader reader, MailboxRef mailbox, MessageId id, DraftContent content, bool isDraft, CancellationToken cancellationToken)
    {
        var read = await reader.GetAsync(mailbox, id, MessageFetch.Default, cancellationToken).ConfigureAwait(false);
        return read.IsSuccess ? read : AsWritten(id, content, isDraft);
    }

    public static MailMessage AsWritten(MessageId id, DraftContent content, bool isDraft) => new(id)
    {
        Subject = content.Subject,
        Body = content.Body,
        From = content.From,
        ToRecipients = content.ToRecipients,
        CcRecipients = content.CcRecipients,
        BccRecipients = content.BccRecipients,
        ReplyTo = content.ReplyTo,
        Importance = content.Importance,
        IsDraft = isDraft,
        IsRead = true,
        HasAttachments = content.Attachments.Count > 0,
    };
}
