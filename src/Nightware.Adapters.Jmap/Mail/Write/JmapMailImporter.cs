using MimeKit;
using Nightware.Adapters.Jmap.Core;
using Nightware.Adapters.Jmap.Core.Blobs;
using Nightware.Adapters.Jmap.Core.Errors;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;
using Nightware.Adapters.Jmap.Mail.Ids;
using Nightware.Application.Common.Identity;
using Nightware.Application.Mail;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Mail.Write;

/// <summary>
/// Imports a migrated message with its original dates and read state: the message is rebuilt as MIME
/// (<c>Date</c> = the original send time) and stored with <c>Email/import</c>, whose <c>receivedAt</c>
/// is the original delivery time (ADR 0001 §36).
/// </summary>
/// <remarks>
/// The original sender is kept: migrated mail is from other people by definition, and storing it sends
/// nothing (send-as is checked when a message is sent). A backend refusal still maps to 403, which
/// NightMail's migration answers by retrying without <c>from</c>.
/// </remarks>
internal sealed class JmapMailImporter(IJmapClient jmap, IJmapBlobService blobs, IUserContext user, TimeProvider time) : IMailImporter
{
    private const string Imported = "imported";

    public async Task<Result<MessageId>> ImportAsync(MailboxRef mailbox, FolderRef folder, DraftContent content, ImportOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);

        var account = await jmap.ResolveAccountAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!account.IsSuccess)
        {
            return account.Error;
        }

        var accountId = account.Value.AccountId;
        var list = new JmapRequestBuilder();
        var get = list.Add(JmapMethods.MailboxGet, WriteMailboxes.GetAll(accountId));
        var listed = await jmap.SendAsync(list, cancellationToken).ConfigureAwait(false);
        var target = listed.Bind(r => r.Get(get)).Bind(r => WriteMailboxes.Resolve(r.List, folder));
        if (!target.IsSuccess)
        {
            return target.Error;
        }

        if (target.Value.IsRoot)
        {
            return Error.Validation("A message cannot be created in the mailbox root.", "folderId");
        }

        var messageId = InternetMessageIds.Mint(mailbox);
        var mime = ImportMime.Build(content, content.From ?? SenderIdentity.DefaultFrom(mailbox, user), messageId, options.SentAt ?? options.ReceivedAt ?? time.GetUtcNow());
        if (!mime.IsSuccess)
        {
            return mime.Error;
        }

        var uploaded = await UploadAsync(accountId, mime.Value, cancellationToken).ConfigureAwait(false);
        if (!uploaded.IsSuccess)
        {
            return uploaded.Error;
        }

        var keywords = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (options.IsRead)
        {
            keywords[Keywords.Seen] = true;
        }

        if (options.IsDraft)
        {
            keywords[Keywords.Draft] = true;
        }

        var request = new JmapRequestBuilder();
        var import = request.Add(JmapMethods.EmailImport, new EmailImportRequest(accountId, new Dictionary<string, EmailImport>(StringComparer.Ordinal)
        {
            [Imported] = new(uploaded.Value, new Dictionary<string, bool>(StringComparer.Ordinal) { [target.Value.MailboxId!] = true })
            {
                Keywords = keywords,
                ReceivedAt = options.ReceivedAt,
            },
        }));
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var created = response.Bind(r => r.Get(import)).Bind(r => r.Created(Imported));
        return created.Bind<MessageId>(email => email.Id is { } id
            ? MailItemId.For(id, keywords, [messageId]).ToMessageId()
            : JmapErrors.Unexpected("The mail server imported a message without an id."));
    }

    private async Task<Result<string>> UploadAsync(string accountId, MimeMessage message, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        await message.WriteToAsync(stream, cancellationToken).ConfigureAwait(false);
        stream.Position = 0;
        var uploaded = await blobs.UploadAsync(accountId, stream, "message/rfc822", cancellationToken).ConfigureAwait(false);
        return uploaded.Map(blob => blob.BlobId);
    }
}

/// <summary>The RFC 5322 form of a migrated message.</summary>
internal static class ImportMime
{
    public static Result<MimeMessage> Build(DraftContent content, EmailAddress from, string messageId, DateTimeOffset date)
    {
        var message = new MimeMessage
        {
            Subject = content.Subject ?? string.Empty,
            Date = date,
            MessageId = messageId,
            Importance = content.Importance switch
            {
                Importance.High => MessageImportance.High,
                Importance.Low => MessageImportance.Low,
                _ => MessageImportance.Normal,
            },
        };

        var addressed = Add(message.From, [from])
            && Add(message.To, content.ToRecipients)
            && Add(message.Cc, content.CcRecipients)
            && Add(message.Bcc, content.BccRecipients)
            && Add(message.ReplyTo, content.ReplyTo);
        if (!addressed)
        {
            return Error.Validation("A recipient address is not valid.", "toRecipients");
        }

        if (content.InReplyTo is { } inReplyTo)
        {
            message.InReplyTo = InternetMessageIds.StripBrackets(inReplyTo);
        }

        foreach (var reference in content.References)
        {
            message.References.Add(InternetMessageIds.StripBrackets(reference));
        }

        foreach (var header in content.ExtraHeaders)
        {
            try
            {
                message.Headers.Add(header.Name, header.Value);
            }
            catch (ArgumentException)
            {
                return Error.Validation($"'{header.Name}' is not a valid header name.", "internetMessageHeaders");
            }
        }

        var body = new BodyBuilder();
        if (content.Body is { ContentType: BodyType.Html } html)
        {
            body.HtmlBody = html.Content;
        }
        else
        {
            body.TextBody = content.Body?.Content ?? string.Empty;
        }

        foreach (var attachment in content.Attachments)
        {
            var type = ContentType.TryParse(AttachmentUploads.MediaType(attachment.ContentType), out var parsed) ? parsed : new ContentType("application", "octet-stream");
            var entity = attachment.IsInline
                ? body.LinkedResources.Add(attachment.Name, attachment.Content.ToArray(), type)
                : body.Attachments.Add(attachment.Name, attachment.Content.ToArray(), type);
            if (!string.IsNullOrWhiteSpace(attachment.ContentId))
            {
                entity.ContentId = attachment.ContentId.Trim().Trim('<', '>');
            }
        }

        message.Body = body.ToMessageBody();
        return message;
    }

    private static bool Add(InternetAddressList list, IEnumerable<EmailAddress> addresses)
    {
        foreach (var address in addresses)
        {
            if (!MailboxAddress.TryParse(address.Address.Trim(), out var parsed))
            {
                return false;
            }

            parsed.Name = string.IsNullOrWhiteSpace(address.Name) ? null : address.Name;
            list.Add(parsed);
        }

        return true;
    }
}
