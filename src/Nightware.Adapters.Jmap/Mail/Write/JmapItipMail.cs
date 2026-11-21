using MimeKit;

using Nightware.Adapters.Jmap.Core;
using Nightware.Adapters.Jmap.Core.Blobs;
using Nightware.Adapters.Jmap.Core.Errors;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;
using Nightware.Adapters.Jmap.Mail.Ids;
using Nightware.Application.Calendar;
using Nightware.Application.Common.Identity;
using Nightware.Application.Mail;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Mail.Write;

/// <summary>
/// <see cref="IItipMail"/>: the iTIP object as iMIP (RFC 6047), a <c>multipart/alternative</c> of the comment as text
/// and a <c>text/calendar; method=…</c> part, stored as a draft in Drafts and sent like any draft
/// (<see cref="IMailSender.SendDraftAsync"/>: the mailbox's identity, the send-as check, Sent Items).
/// </summary>
internal sealed class JmapItipMail(IJmapClient jmap, IJmapBlobService blobs, IMailSender sender, IUserContext user, TimeProvider time) : IItipMail
{
    private const string Imported = "itip";

    public async Task<Result> SendAsync(MailboxRef from, ItipMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var account = await jmap.ResolveAccountAsync(from, cancellationToken).ConfigureAwait(false);
        if (!account.IsSuccess)
        {
            return account.Error;
        }

        var accountId = account.Value.AccountId;
        var list = new JmapRequestBuilder();
        var get = list.Add(JmapMethods.MailboxGet, WriteMailboxes.GetAll(accountId));
        var drafts = (await jmap.SendAsync(list, cancellationToken).ConfigureAwait(false))
            .Bind(r => r.Get(get))
            .Bind(r => WriteMailboxes.Resolve(r.List, new FolderRef.WellKnown(WellKnownFolder.Drafts)));
        if (!drafts.IsSuccess)
        {
            return drafts.Error;
        }

        var messageId = InternetMessageIds.Mint(from);
        var sending = SenderIdentity.DefaultFrom(from, user);
        using var stream = new MemoryStream();
        await Build(message, sending, messageId, time.GetUtcNow()).WriteToAsync(stream, cancellationToken).ConfigureAwait(false);
        stream.Position = 0;
        var uploaded = await blobs.UploadAsync(accountId, stream, "message/rfc822", cancellationToken).ConfigureAwait(false);
        if (!uploaded.IsSuccess)
        {
            return uploaded.Error;
        }

        var keywords = new Dictionary<string, bool>(StringComparer.Ordinal) { [Keywords.Draft] = true, [Keywords.Seen] = true };
        var request = new JmapRequestBuilder();
        var import = request.Add(JmapMethods.EmailImport, new EmailImportRequest(accountId, new Dictionary<string, EmailImport>(StringComparer.Ordinal)
        {
            [Imported] = new(uploaded.Value.BlobId, new Dictionary<string, bool>(StringComparer.Ordinal) { [drafts.Value.MailboxId!] = true }) { Keywords = keywords },
        }));
        var created = (await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false)).Bind(r => r.Get(import)).Bind(r => r.Created(Imported));
        if (!created.IsSuccess)
        {
            return created.Error;
        }

        if (created.Value.Id is not { } id)
        {
            return JmapErrors.Unexpected("The mail server imported the reply without an id.");
        }

        var sent = await sender.SendDraftAsync(from, MailItemId.For(id, keywords, [messageId]).ToMessageId(), cancellationToken).ConfigureAwait(false);
        if (!sent.IsSuccess)
        {
            // Not left in Drafts for the user to find: the answer failed and says so. Best effort.
            var discard = new JmapRequestBuilder();
            discard.Add(JmapMethods.EmailSet, new SetRequest<Email>(accountId) { Destroy = [id] });
            await jmap.SendAsync(discard, cancellationToken).ConfigureAwait(false);
        }

        return sent;
    }

    internal static MimeMessage Build(ItipMessage message, EmailAddress from, string messageId, DateTimeOffset date)
    {
        var mime = new MimeMessage { Subject = message.Subject, Date = date, MessageId = messageId };
        mime.From.Add(new MailboxAddress(from.Name ?? string.Empty, from.Address));
        mime.To.Add(MailboxAddress.Parse(message.To));
        var calendar = new TextPart("calendar") { Text = message.Calendar };
        calendar.ContentType.Parameters.Add("method", message.Method);
        calendar.ContentType.Charset = "utf-8";
        mime.Body = new MultipartAlternative { new TextPart("plain") { Text = message.Body }, calendar };
        return mime;
    }
}
