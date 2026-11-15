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
/// Drafts in the Drafts mailbox. A draft is addressed as <c>d.&lt;base64url(Message-ID)&gt;</c>: the façade
/// mints the Message-ID when it creates the draft and keeps it on every replacement, so the Graph id the
/// client holds stays valid although each change creates a new JMAP Email (ADR 0001 §1).
/// </summary>
/// <remarks>
/// A replacement is two requests: create the new Email, then destroy the old one. In one <c>Email/set</c>
/// the destroy would run even when the create failed, losing the draft. If the destroy fails after a
/// successful create, two Emails share the Message-ID; that is reported as a failure rather than hidden.
/// Existing attachments move to the replacement by <c>blobId</c>: nothing is downloaded or uploaded again.
/// </remarks>
internal sealed class JmapDraftStore(IJmapClient jmap, IJmapBlobService blobs, IJmapMessageLocator locator, IUserContext user) : IDraftStore
{
    private const string Draft = "draft";

    public async Task<Result<MessageId>> CreateAsync(MailboxRef mailbox, DraftContent content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var account = await jmap.ResolveAccountAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!account.IsSuccess)
        {
            return account.Error;
        }

        var accountId = account.Value.AccountId;
        var mailboxes = await MailboxesAsync(accountId, cancellationToken).ConfigureAwait(false);
        if (!mailboxes.IsSuccess)
        {
            return mailboxes.Error;
        }

        var drafts = WriteMailboxes.IdOfRole(mailboxes.Value, MailboxRoles.Drafts);
        if (drafts is null)
        {
            return Error.NotFound("The mailbox has no Drafts folder.");
        }

        var attachments = await DraftAttachments.ForContentAsync(jmap, blobs, locator, account.Value, content, cancellationToken).ConfigureAwait(false);
        if (!attachments.IsSuccess)
        {
            return attachments.Error;
        }

        var messageId = InternetMessageIds.Mint(mailbox);
        var outgoing = OutgoingEmail.FromContent(
            content,
            content.From ?? SenderIdentity.DefaultFrom(mailbox, user),
            messageId,
            new Dictionary<string, bool>(StringComparer.Ordinal) { [drafts] = true },
            OutgoingEmail.DraftKeywords,
            attachments.Value);
        if (!outgoing.IsSuccess)
        {
            return outgoing.Error;
        }

        var created = await CreateEmailAsync(accountId, outgoing.Value, cancellationToken).ConfigureAwait(false);
        return created.IsSuccess ? MailItemId.ForDraft(messageId).ToMessageId() : created.Error;
    }

    public async Task<Result<MessageId>> ReplaceAsync(MailboxRef mailbox, MessageId draftId, DraftContent content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var existing = await DraftAsync(mailbox, draftId, cancellationToken).ConfigureAwait(false);
        if (!existing.IsSuccess)
        {
            return existing.Error;
        }

        var (located, email) = existing.Value;
        var added = await DraftAttachments.ForContentAsync(jmap, blobs, locator, located.Account, content, cancellationToken).ConfigureAwait(false);
        if (!added.IsSuccess)
        {
            return added.Error;
        }

        // The PATCH carries content only; what it does not describe stays with the draft: its mailboxes,
        // keywords, sender, threading headers (a reply draft must still thread) and attachments.
        var messageId = ExistingEmail.MessageId(email) ?? InternetMessageIds.Mint(mailbox);
        var outgoing = OutgoingEmail.FromContent(
            content,
            content.From ?? Sender(email) ?? SenderIdentity.DefaultFrom(mailbox, user),
            messageId,
            email.MailboxIds ?? new Dictionary<string, bool>(),
            ExistingEmail.DraftKeywords(email),
            [.. ExistingEmail.Attachments(email), .. added.Value]);
        if (!outgoing.IsSuccess)
        {
            return outgoing.Error;
        }

        var replacement = outgoing.Value with
        {
            InReplyTo = content.InReplyTo is null ? email.InReplyTo ?? [] : outgoing.Value.InReplyTo,
            References = content.References.Count == 0 ? email.References ?? [] : outgoing.Value.References,
        };
        var replaced = await ReplaceEmailAsync(located, replacement, cancellationToken).ConfigureAwait(false);
        return replaced.IsSuccess ? MailItemId.ForDraft(messageId).ToMessageId() : replaced.Error;
    }

    public async Task<Result<MailAttachment>> AddAttachmentAsync(MailboxRef mailbox, MessageId draftId, NewAttachment attachment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        var existing = await DraftAsync(mailbox, draftId, cancellationToken).ConfigureAwait(false);
        if (!existing.IsSuccess)
        {
            return existing.Error;
        }

        var (located, email) = existing.Value;
        var uploaded = await AttachmentUploads.UploadAsync(blobs, located.AccountId, attachment, cancellationToken).ConfigureAwait(false);
        if (!uploaded.IsSuccess)
        {
            return uploaded.Error;
        }

        var snapshot = Snapshot(mailbox, email);
        var replacement = snapshot with { Attachments = [.. snapshot.Attachments, uploaded.Value] };
        var replaced = await ReplaceEmailAsync(located, replacement, cancellationToken, readAttachments: true).ConfigureAwait(false);
        if (!replaced.IsSuccess)
        {
            return replaced.Error;
        }

        // Attachment ids are partIds of the current Email (ADR 0001 §5); the new part is the last one
        // with this name and size, since it was appended after every carried attachment.
        var parts = replaced.Value;
        var part = parts.LastOrDefault(p => p.Name == attachment.Name && p.Size == attachment.Content.Length) ?? (parts.Count > 0 ? parts[^1] : null);
        if (part?.PartId is null)
        {
            return JmapErrors.Unexpected("The mail server did not report the new attachment.");
        }

        return new MailAttachment(new AttachmentId(part.PartId), attachment.Name, part.Type ?? uploaded.Value.Type!, part.Size ?? attachment.Content.Length, AttachmentKind.File)
        {
            IsInline = attachment.IsInline,
            ContentId = part.Cid,
        };
    }

    public async Task<Result> RemoveAttachmentAsync(MailboxRef mailbox, MessageId draftId, AttachmentId attachment, CancellationToken cancellationToken)
    {
        var existing = await DraftAsync(mailbox, draftId, cancellationToken).ConfigureAwait(false);
        if (!existing.IsSuccess)
        {
            return existing.Error;
        }

        var (located, email) = existing.Value;
        var snapshot = Snapshot(mailbox, email);
        var kept = snapshot.Attachments.Where(p => !string.Equals(p.PartId, attachment.Value, StringComparison.Ordinal)).ToList();
        if (kept.Count == snapshot.Attachments.Count)
        {
            return Error.NotFound("The specified attachment was not found on the draft.");
        }

        var replaced = await ReplaceEmailAsync(located, snapshot with { Attachments = kept }, cancellationToken).ConfigureAwait(false);
        return replaced.ToResult();
    }

    private static OutgoingEmail Snapshot(MailboxRef mailbox, Email email) =>
        ExistingEmail.Snapshot(email, ExistingEmail.MessageId(email) ?? InternetMessageIds.Mint(mailbox));

    private static EmailAddress? Sender(Email email) =>
        email.From is [var from, ..] ? new EmailAddress(from.Email, from.Name) : null;

    /// <summary>The draft behind <paramref name="draftId"/>, read in full; anything that is not a draft is refused.</summary>
    private async Task<Result<(LocatedMessage Located, Email Email)>> DraftAsync(MailboxRef mailbox, MessageId draftId, CancellationToken cancellationToken)
    {
        var located = await jmap.LocateAsync(locator, mailbox, draftId, cancellationToken).ConfigureAwait(false);
        if (!located.IsSuccess)
        {
            return located.Error;
        }

        var request = new JmapRequestBuilder();
        var get = request.Add(JmapMethods.EmailGet, ExistingEmail.Get(located.Value.AccountId, located.Value.JmapId));
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var email = response.Bind(r => r.Get(get)).Bind(WriteSupport.Single);
        if (!email.IsSuccess)
        {
            return email.Error;
        }

        return ExistingEmail.IsDraft(email.Value) ? (located.Value, email.Value) : WriteSupport.NotADraft();
    }

    private async Task<Result<IReadOnlyList<Mailbox>>> MailboxesAsync(string accountId, CancellationToken cancellationToken)
    {
        var request = new JmapRequestBuilder();
        var get = request.Add(JmapMethods.MailboxGet, WriteMailboxes.GetAll(accountId));
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Bind(r => r.Get(get)).Map(r => r.List);
    }

    private async Task<Result<Email>> CreateEmailAsync(string accountId, OutgoingEmail email, CancellationToken cancellationToken)
    {
        var request = new JmapRequestBuilder();
        var set = request.Add(JmapMethods.EmailSet, Create(accountId, email));
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Bind(r => r.Get(set)).Bind(s => s.Created(Draft));
    }

    /// <summary>Creates the replacement, then destroys the original; returns the replacement's attachments when asked.</summary>
    private async Task<Result<IReadOnlyList<EmailBodyPart>>> ReplaceEmailAsync(
        LocatedMessage original, OutgoingEmail replacement, CancellationToken cancellationToken, bool readAttachments = false)
    {
        var create = new JmapRequestBuilder();
        var set = create.Add(JmapMethods.EmailSet, Create(original.AccountId, replacement));
        JmapCall<GetResponse<Email>>? get = null;
        if (readAttachments)
        {
            get = create.Add(JmapMethods.EmailGet, new EmailGetRequest(original.AccountId)
            {
                Ids = ["#" + Draft],
                Properties = ["id", "attachments"],
                BodyProperties = ExistingEmail.BodyProperties,
            });
        }

        var created = await jmap.SendAsync(create, cancellationToken).ConfigureAwait(false);
        if (!created.IsSuccess)
        {
            return created.Error;
        }

        var made = created.Value.Get(set).Bind(s => s.Created(Draft));
        if (!made.IsSuccess)
        {
            return made.Error;
        }

        IReadOnlyList<EmailBodyPart> attachments = [];
        if (get is not null)
        {
            var read = created.Value.Get(get).Bind(WriteSupport.Single);
            if (!read.IsSuccess)
            {
                return read.Error;
            }

            attachments = read.Value.Attachments ?? [];
        }

        var destroy = new JmapRequestBuilder();
        var destroySet = destroy.Add(JmapMethods.EmailSet, new SetRequest<Email>(original.AccountId) { Destroy = [original.JmapId] });
        var destroyed = await jmap.SendAsync(destroy, cancellationToken).ConfigureAwait(false);
        var outcome = destroyed.Bind(r => r.Get(destroySet)).Then(s => s.Destroyed(original.JmapId));
        return outcome.IsSuccess ? Result<IReadOnlyList<EmailBodyPart>>.Success(attachments) : outcome.Error!;
    }

    private static SetRequest<Email> Create(string accountId, OutgoingEmail email) => new(accountId)
    {
        Create = new Dictionary<string, Email>(StringComparer.Ordinal) { [Draft] = email.ToEmail() },
    };
}
