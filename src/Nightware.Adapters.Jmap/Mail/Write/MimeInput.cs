using MimeKit;
using Nightware.Adapters.Jmap.Core.Blobs;
using Nightware.Adapters.Jmap.Mail.Ids;
using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Mail.Write;

/// <summary>
/// A message the client sent as MIME (Graph's <c>text/plain</c> base64 form of <c>POST messages</c> and <c>sendMail</c>),
/// readied for <c>Email/import</c>: parsed, given a sender when it has none, and given the façade's own Message-ID.
/// </summary>
/// <remarks>
/// The Message-ID is always minted: a draft's Graph id is built from it (<c>d.…</c>, ADR 0001 §1), and a client's own
/// value could already name another message in the mailbox. Everything else in the MIME is kept as sent.
/// </remarks>
internal static class MimeInput
{
    /// <summary>The parsed message with a minted Message-ID and a <c>From</c> (the mailbox's own when it had none).</summary>
    public static Result<(MimeMessage Message, string MessageId, EmailAddress From)> Prepare(ReadOnlyMemory<byte> mime, MailboxRef mailbox, IUserContext user)
    {
        MimeMessage message;
        try
        {
            using var stream = new MemoryStream(mime.ToArray(), writable: false);
            message = MimeMessage.Load(stream);
        }
        catch (FormatException)
        {
            return Error.Validation("The MIME content is not a valid message.");
        }

        if (message.Body is null)
        {
            return Error.Validation("The MIME content is not a valid message.");
        }

        EmailAddress from;
        if (message.From.Mailboxes.FirstOrDefault() is { } sender)
        {
            from = new EmailAddress(sender.Address, string.IsNullOrWhiteSpace(sender.Name) ? null : sender.Name);
        }
        else
        {
            from = SenderIdentity.DefaultFrom(mailbox, user);
            message.From.Add(new MailboxAddress(from.Name, from.Address));
        }

        var messageId = InternetMessageIds.Mint(mailbox);
        message.MessageId = messageId;
        if (!message.Headers.Contains(HeaderId.Date))
        {
            message.Date = DateTimeOffset.UtcNow;
        }

        return (message, messageId, from);
    }

    public static async Task<Result<string>> UploadAsync(IJmapBlobService blobs, string accountId, MimeMessage message, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        await message.WriteToAsync(stream, cancellationToken).ConfigureAwait(false);
        stream.Position = 0;
        var uploaded = await blobs.UploadAsync(accountId, stream, "message/rfc822", cancellationToken).ConfigureAwait(false);
        return uploaded.Map(static blob => blob.BlobId);
    }
}
