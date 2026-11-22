using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Nightware.Adapters.Jmap.Core;
using Nightware.Adapters.Jmap.Core.Blobs;
using Nightware.Adapters.Jmap.Core.Errors;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;
using Nightware.Adapters.Jmap.Core.Session;
using Nightware.Adapters.Jmap.Mail.Ids;
using Nightware.Application.Common.Identity;
using Nightware.Application.Mail;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Mail.Write;

/// <summary>
/// Sends through JMAP <c>EmailSubmission/set</c>. On success the server itself files the message: moved to
/// Sent Items with <c>$draft</c> cleared (<c>onSuccessUpdateEmail</c>), or destroyed when it is not to be
/// saved (<c>onSuccessDestroyEmail</c>). The envelope is left to the server, which derives it from the
/// headers (Bcc included) and strips Bcc from what it delivers — verified on Stalwart v1.1.
/// </summary>
internal sealed partial class JmapMailSender(
    IJmapClient jmap,
    IJmapBlobService blobs,
    IJmapMessageLocator locator,
    IUserContext user,
    ILogger<JmapMailSender> logger) : IMailSender
{
    private const string Message = "message";
    private const string Submission = "send";
    private const string NewIdentity = "identity";

    private static readonly IReadOnlyList<string> DraftProperties = ["id", "from", "keywords", "mailboxIds"];

    public async Task<Result> SendDraftAsync(MailboxRef mailbox, MessageId draftId, CancellationToken cancellationToken)
    {
        var located = await jmap.LocateAsync(locator, mailbox, draftId, cancellationToken).ConfigureAwait(false);
        if (!located.IsSuccess)
        {
            return located.Error;
        }

        var (account, jmapId) = (located.Value.Account, located.Value.JmapId);
        var request = new JmapRequestBuilder();
        var draftGet = request.Add(JmapMethods.EmailGet, new EmailGetRequest(account.AccountId) { Ids = [jmapId], Properties = DraftProperties });
        var context = AddContext(request, account.AccountId);
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return response.Error;
        }

        var draft = response.Value.Get(draftGet).Bind(WriteSupport.Single);
        if (!draft.IsSuccess)
        {
            return draft.Error;
        }

        if (!ExistingEmail.IsDraft(draft.Value))
        {
            return WriteSupport.NotADraft();
        }

        var read = context.Read(response.Value);
        if (!read.IsSuccess)
        {
            return read.Error;
        }

        var from = draft.Value.From is [var sender, ..]
            ? new EmailAddress(sender.Email, sender.Name)
            : SenderIdentity.DefaultFrom(mailbox, user);
        var identity = await IdentityAsync(account, read.Value.Identities, mailbox, from, cancellationToken).ConfigureAwait(false);
        if (!identity.IsSuccess)
        {
            return identity.Error;
        }

        var submit = new JmapRequestBuilder();
        var submission = AddSubmission(submit, account.AccountId, jmapId, identity.Value, read.Value.Mailboxes, saveToSentItems: true);
        var submitted = await jmap.SendAsync(submit, cancellationToken).ConfigureAwait(false);
        return submitted.IsSuccess ? Submitted(submitted.Value, submission) : submitted.Error;
    }

    public async Task<Result> SendAsync(MailboxRef mailbox, DraftContent content, bool saveToSentItems, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var account = await jmap.ResolveAccountAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!account.IsSuccess)
        {
            return account.Error;
        }

        var accountId = account.Value.AccountId;
        var request = new JmapRequestBuilder();
        var context = AddContext(request, accountId);
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var read = response.Bind(context.Read);
        if (!read.IsSuccess)
        {
            return read.Error;
        }

        var from = content.From ?? SenderIdentity.DefaultFrom(mailbox, user);
        var identity = await IdentityAsync(account.Value, read.Value.Identities, mailbox, from, cancellationToken).ConfigureAwait(false);
        if (!identity.IsSuccess)
        {
            return identity.Error;
        }

        // The message is stored before it is submitted (JMAP submits Emails, not MIME). It starts as a
        // draft so that a failed submission leaves nothing in Sent Items.
        var home = WriteMailboxes.IdOfRole(read.Value.Mailboxes, MailboxRoles.Drafts) ?? WriteMailboxes.IdOfRole(read.Value.Mailboxes, MailboxRoles.Sent);
        if (home is null)
        {
            return Error.NotFound("The mailbox has no Drafts or Sent Items folder.");
        }

        var attachments = await DraftAttachments.ForContentAsync(jmap, blobs, locator, account.Value, content, cancellationToken).ConfigureAwait(false);
        if (!attachments.IsSuccess)
        {
            return attachments.Error;
        }

        var outgoing = OutgoingEmail.FromContent(
            content,
            from,
            InternetMessageIds.Mint(mailbox),
            new Dictionary<string, bool>(StringComparer.Ordinal) { [home] = true },
            OutgoingEmail.DraftKeywords,
            attachments.Value);
        if (!outgoing.IsSuccess)
        {
            return outgoing.Error;
        }

        // Create and submit go in one request. Split, a failure after the first HTTP request would
        // leave the created copy in Drafts with nothing to clean it up (the discard below needs both
        // responses).
        var send = new JmapRequestBuilder().RequireSingleRequest();
        var create = send.Add(JmapMethods.EmailSet, new SetRequest<Email>(accountId)
        {
            Create = new Dictionary<string, Email>(StringComparer.Ordinal) { [Message] = outgoing.Value.ToEmail() },
        });
        var submission = AddSubmission(send, accountId, "#" + Message, identity.Value, read.Value.Mailboxes, saveToSentItems);
        var sent = await jmap.SendAsync(send, cancellationToken).ConfigureAwait(false);
        if (!sent.IsSuccess)
        {
            return sent.Error;
        }

        var created = sent.Value.Get(create).Bind(s => s.Created(Message));
        if (!created.IsSuccess)
        {
            return created.Error;
        }

        var outcome = Submitted(sent.Value, submission);
        if (!outcome.IsSuccess && created.Value.Id is { } stored)
        {
            await DiscardAsync(accountId, stored, cancellationToken).ConfigureAwait(false);
        }

        return outcome;
    }

    /// <summary>
    /// The MIME, as sent but with a minted Message-ID: imported into Drafts and submitted in one request (JMAP submits
    /// Emails, not MIME), then filed in Sent Items by the server, as <see cref="SendAsync"/> does. The server derives the
    /// envelope from the headers, Bcc included, and strips Bcc from what it delivers.
    /// </summary>
    public async Task<Result> SendMimeAsync(MailboxRef mailbox, ReadOnlyMemory<byte> mime, CancellationToken cancellationToken)
    {
        var prepared = MimeInput.Prepare(mime, mailbox, user);
        if (!prepared.IsSuccess)
        {
            return prepared.Error;
        }

        var account = await jmap.ResolveAccountAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!account.IsSuccess)
        {
            return account.Error;
        }

        var accountId = account.Value.AccountId;
        var request = new JmapRequestBuilder();
        var context = AddContext(request, accountId);
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var read = response.Bind(context.Read);
        if (!read.IsSuccess)
        {
            return read.Error;
        }

        var identity = await IdentityAsync(account.Value, read.Value.Identities, mailbox, prepared.Value.From, cancellationToken).ConfigureAwait(false);
        if (!identity.IsSuccess)
        {
            return identity.Error;
        }

        var home = WriteMailboxes.IdOfRole(read.Value.Mailboxes, MailboxRoles.Drafts) ?? WriteMailboxes.IdOfRole(read.Value.Mailboxes, MailboxRoles.Sent);
        if (home is null)
        {
            return Error.NotFound("The mailbox has no Drafts or Sent Items folder.");
        }

        var blob = await MimeInput.UploadAsync(blobs, accountId, prepared.Value.Message, cancellationToken).ConfigureAwait(false);
        if (!blob.IsSuccess)
        {
            return blob.Error;
        }

        var send = new JmapRequestBuilder().RequireSingleRequest();
        var import = send.Add(JmapMethods.EmailImport, new EmailImportRequest(accountId, new Dictionary<string, EmailImport>(StringComparer.Ordinal)
        {
            [Message] = new(blob.Value, new Dictionary<string, bool>(StringComparer.Ordinal) { [home] = true }) { Keywords = OutgoingEmail.DraftKeywords },
        }));
        var submission = AddSubmission(send, accountId, "#" + Message, identity.Value, read.Value.Mailboxes, saveToSentItems: true);
        var sent = await jmap.SendAsync(send, cancellationToken).ConfigureAwait(false);
        if (!sent.IsSuccess)
        {
            return sent.Error;
        }

        var created = sent.Value.Get(import).Bind(r => r.Created(Message));
        if (!created.IsSuccess)
        {
            return created.Error;
        }

        var outcome = Submitted(sent.Value, submission);
        if (!outcome.IsSuccess && created.Value.Id is { } stored)
        {
            await DiscardAsync(accountId, stored, cancellationToken).ConfigureAwait(false);
        }

        return outcome;
    }

    /// <summary>The identity sending as <paramref name="from"/>, created for the mailbox's own address when it has none.</summary>
    private async Task<Result<string>> IdentityAsync(JmapAccount account, IReadOnlyList<Identity> identities, MailboxRef mailbox, EmailAddress from, CancellationToken cancellationToken)
    {
        if (SenderIdentity.Find(identities, from.Address)?.Id is { } existing)
        {
            return existing;
        }

        if (!SenderIdentity.MayCreateFor(mailbox, from.Address))
        {
            return SenderIdentity.SendAsDenied(from.Address);
        }

        var request = new JmapRequestBuilder();
        var set = request.Add(JmapMethods.IdentitySet, new SetRequest<Identity>(account.AccountId)
        {
            Create = new Dictionary<string, Identity>(StringComparer.Ordinal)
            {
                [NewIdentity] = new Identity { Name = from.Name ?? string.Empty, Email = from.Address.Trim() },
            },
        });
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var created = response.Bind(r => r.Get(set)).Bind(s => s.Created(NewIdentity));
        return created.Bind<string>(i => i.Id is { } id ? id : JmapErrors.Unexpected("The mail server created an identity without an id."));
    }

    private static SendContext AddContext(JmapRequestBuilder request, string accountId) => new(
        request.Add(JmapMethods.MailboxGet, WriteMailboxes.GetAll(accountId)),
        request.Add(JmapMethods.IdentityGet, new GetRequest(accountId)));

    private static JmapCall<SetResponse<EmailSubmission>> AddSubmission(
        JmapRequestBuilder request, string accountId, string emailId, string identityId, IReadOnlyList<Mailbox> mailboxes, bool saveToSentItems)
    {
        var onSuccess = $"#{Submission}";
        return request.Add(JmapMethods.EmailSubmissionSet, new EmailSubmissionSetRequest(accountId)
        {
            Create = new Dictionary<string, EmailSubmission>(StringComparer.Ordinal)
            {
                [Submission] = new EmailSubmission { IdentityId = identityId, EmailId = emailId },
            },
            OnSuccessUpdateEmail = saveToSentItems
                ? new Dictionary<string, PatchObject>(StringComparer.Ordinal) { [onSuccess] = SentPatch(mailboxes) }
                : null,
            OnSuccessDestroyEmail = saveToSentItems ? null : [onSuccess],
        });
    }

    /// <summary>Files a sent message: into Sent Items (when the mailbox has one), no longer a draft, read.</summary>
    private static PatchObject SentPatch(IReadOnlyList<Mailbox> mailboxes)
    {
        var patch = new Patch()
            .SetEntry("keywords", Keywords.Draft, false)
            .SetEntry("keywords", Keywords.Seen, true);
        if (WriteMailboxes.IdOfRole(mailboxes, MailboxRoles.Sent) is { } sent)
        {
            patch.Set("mailboxIds", new JsonObject { [sent] = true });
        }

        return patch.Build();
    }

    /// <summary>
    /// The submission's outcome. Once it is accepted the message is on its way, so a failure to file it
    /// afterwards is logged, not returned: an error would make the user send it again.
    /// </summary>
    private Result Submitted(JmapResponse response, JmapCall<SetResponse<EmailSubmission>> submission)
    {
        var submitted = response.Get(submission).Bind(s => s.Created(Submission));
        if (!submitted.IsSuccess)
        {
            return submitted.Error;
        }

        var filed = response.GetAdditional(submission, JmapMethods.EmailSet).Then(s => s.AllSucceeded());
        if (!filed.IsSuccess)
        {
            LogNotFiled(logger, filed.Error!.Message);
        }

        return Result.Success();
    }

    private async Task DiscardAsync(string accountId, string emailId, CancellationToken cancellationToken)
    {
        var request = new JmapRequestBuilder();
        var set = request.Add(JmapMethods.EmailSet, new SetRequest<Email>(accountId) { Destroy = [emailId] });
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var destroyed = response.Bind(r => r.Get(set)).Then(s => s.Destroyed(emailId));
        if (!destroyed.IsSuccess)
        {
            LogNotDiscarded(logger, destroyed.Error!.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A message was sent but could not be filed afterwards: {Reason}")]
    private static partial void LogNotFiled(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A message whose submission failed could not be removed from Drafts: {Reason}")]
    private static partial void LogNotDiscarded(ILogger logger, string reason);

    private sealed record SendContext(JmapCall<GetResponse<Mailbox>> Mailboxes, JmapCall<GetResponse<Identity>> Identities)
    {
        public Result<(IReadOnlyList<Mailbox> Mailboxes, IReadOnlyList<Identity> Identities)> Read(JmapResponse response)
        {
            var mailboxes = response.Get(Mailboxes);
            if (!mailboxes.IsSuccess)
            {
                return mailboxes.Error;
            }

            return response.Get(Identities).Map(i => (mailboxes.Value.List, i.List));
        }
    }
}
