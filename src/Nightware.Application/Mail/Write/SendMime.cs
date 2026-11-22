using Nightware.Domain.Kernel;

namespace Nightware.Application.Mail.Write;

/// <summary>Graph <c>POST sendMail</c> with a MIME body (<c>text/plain</c>, base64): sends it, saved to Sent Items (202).</summary>
public sealed class SendMime(IMailSender sender)
{
    public async Task<Result> ExecuteAsync(MailboxRef mailbox, ReadOnlyMemory<byte> mime, CancellationToken cancellationToken) =>
        MimeLimit.Check(mime) is { } invalid ? invalid : await sender.SendMimeAsync(mailbox, mime, cancellationToken).ConfigureAwait(false);
}
