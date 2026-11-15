using Nightware.Adapters.Jmap.Core;
using Nightware.Adapters.Jmap.Core.Blobs;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;
using Nightware.Adapters.Jmap.Core.Session;
using Nightware.Adapters.Jmap.Mail.Ids;
using Nightware.Application.Common.Query;
using Nightware.Application.Mail;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Mail.Read;

/// <summary><see cref="IMailMessageReader"/> over JMAP <c>Email/query</c>, <c>Email/get</c>, <c>Thread/get</c> and blob downloads.</summary>
internal sealed class JmapMailMessageReader(IJmapClient jmap, IJmapBlobService blobs, IJmapMessageLocator locator) : IMailMessageReader
{
    private const string RawMessageType = "message/rfc822";

    private static readonly string[] AttachmentProperties = ["id", "receivedAt", "attachments", "bodyStructure"];

    public async Task<Result<Page<MailMessage>>> ListAsync(MailboxRef mailbox, FolderRef? folder, ItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var plan = MessageQueryTranslator.Translate(query);
        if (!plan.IsSuccess)
        {
            return plan.Error;
        }

        var offset = Paging.Offset(query);
        if (!offset.IsSuccess)
        {
            return offset.Error;
        }

        var account = await jmap.ResolveAccountAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!account.IsSuccess)
        {
            return account.Error;
        }

        // GET /messages spans every mailbox, Deleted Items and Junk included, as Graph does.
        string? folderId = null;
        if (folder is not null)
        {
            if (MailboxRoles.IsRoot(folder))
            {
                return Page<MailMessage>.Empty;
            }

            var mailboxes = await JmapMailboxes.LoadAsync(jmap, account.Value, cancellationToken).ConfigureAwait(false);
            var resolved = mailboxes.Bind(m => m.Resolve(folder));
            if (!resolved.IsSuccess)
            {
                return resolved.Error;
            }

            folderId = resolved.Value.Id;
        }

        return plan.Value switch
        {
            MessageQueryPlan.Conversation conversation =>
                await ConversationAsync(account.Value, folderId, conversation, Paging.Size(query, int.MaxValue), cancellationToken).ConfigureAwait(false),
            MessageQueryPlan.Query listing =>
                await QueryAsync(account.Value, folderId, listing, offset.Value, query, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException("Unknown message query plan."),
        };
    }

    public async Task<Result<MailMessage>> GetAsync(MailboxRef mailbox, MessageId id, MessageFetch fetch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        var found = await FetchAsync(mailbox, id, (accountId, jmapId) => EmailProperties.FullRequest(accountId, jmapId, fetch), cancellationToken).ConfigureAwait(false);
        return found.Map(f => EmailMapper.ToMessage(f.Email, preferredBody: fetch.PreferredBodyType));
    }

    public async Task<Result<BlobContent>> GetRawAsync(MailboxRef mailbox, MessageId id, CancellationToken cancellationToken)
    {
        var found = await FetchAsync(mailbox, id, static (accountId, jmapId) => new EmailGetRequest(accountId) { Ids = [jmapId], Properties = ["id", "blobId"] }, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            return found.Error;
        }

        var (account, email) = found.Value;
        return email.BlobId is { } blobId
            ? await blobs.DownloadAsync(account.AccountId, blobId, "message.eml", RawMessageType, cancellationToken).ConfigureAwait(false)
            : MessageNotFound();
    }

    public async Task<Result<IReadOnlyList<MailAttachment>>> ListAttachmentsAsync(MailboxRef mailbox, MessageId id, CancellationToken cancellationToken)
    {
        var found = await FetchAsync(mailbox, id, AttachmentsRequest, cancellationToken).ConfigureAwait(false);
        return found.Map(static f => MimeParts.Attachments(f.Email));
    }

    public async Task<Result<MailAttachment>> GetAttachmentAsync(MailboxRef mailbox, MessageId id, AttachmentId attachment, bool includeContent, CancellationToken cancellationToken)
    {
        var part = await FindPartAsync(mailbox, id, attachment, cancellationToken).ConfigureAwait(false);
        if (!part.IsSuccess)
        {
            return part.Error;
        }

        var (account, email, bodyPart) = part.Value;
        var result = MimeParts.ToAttachment(bodyPart, attachment, email.ReceivedAt);
        if (!includeContent || result.Kind != AttachmentKind.File)
        {
            return result;
        }

        var content = await DownloadAsync(account, bodyPart, cancellationToken).ConfigureAwait(false);
        if (!content.IsSuccess)
        {
            return content.Error;
        }

        var stream = await content.Value.Open(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return result with { ContentBytes = buffer.ToArray() };
        }
    }

    public async Task<Result<BlobContent>> GetAttachmentContentAsync(MailboxRef mailbox, MessageId id, AttachmentId attachment, CancellationToken cancellationToken)
    {
        var part = await FindPartAsync(mailbox, id, attachment, cancellationToken).ConfigureAwait(false);
        return part.IsSuccess
            ? await DownloadAsync(part.Value.Account, part.Value.Part, cancellationToken).ConfigureAwait(false)
            : part.Error;
    }

    private async Task<Result<Page<MailMessage>>> QueryAsync(JmapAccount account, string? folderId, MessageQueryPlan.Query plan, int offset, ItemQuery query, CancellationToken cancellationToken)
    {
        // The query's ids feed Email/get by back-reference, which is not chunked: keep the page
        // within maxObjectsInGet.
        var limit = Paging.Size(query, account.Session.Limits.MaxObjectsInGet);
        var filter = folderId is null
            ? plan.Filter
            : plan.Filter is null ? new EmailFilter { InMailbox = folderId } : EmailFilter.And(new EmailFilter { InMailbox = folderId }, plan.Filter);

        var request = new JmapRequestBuilder();
        var emailQuery = request.Add(JmapMethods.EmailQuery, new EmailQueryRequest(account.AccountId)
        {
            Filter = filter,
            Sort = plan.Sort,
            Position = offset,
            Limit = limit,
            CalculateTotal = plan.Paged ? true : null,
        });
        var get = request.Add(JmapMethods.EmailGet, EmailProperties.ListRequest(account.AccountId), emailQuery.Ref("ids", JmapPaths.Ids));
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return response.Error;
        }

        var ids = response.Value.Get(emailQuery);
        var emails = response.Value.Get(get);
        if (!ids.IsSuccess)
        {
            return ids.Error;
        }

        if (!emails.IsSuccess)
        {
            return emails.Error;
        }

        var messages = InOrder(ids.Value.Ids, emails.Value.List).Select(e => EmailMapper.ToMessage(e, folderId)).ToList();
        var next = plan.Paged && ids.Value.Total is { } total ? Paging.Next(offset, ids.Value.Ids.Count, total) : null;
        return new Page<MailMessage>(messages, next);
    }

    /// <summary>Every message of the named threads, newest first, minus those filed in an excluded folder.</summary>
    private async Task<Result<Page<MailMessage>>> ConversationAsync(JmapAccount account, string? folderId, MessageQueryPlan.Conversation plan, int top, CancellationToken cancellationToken)
    {
        // Graph-style ids the client may still hold can never name a JMAP thread; asking for them
        // would be an invalidArguments error rather than "no such thread".
        var threadIds = plan.ThreadIds.Where(MailItemId.IsValidJmapId).Distinct(StringComparer.Ordinal).ToList();
        if (threadIds.Count == 0)
        {
            return Page<MailMessage>.Empty;
        }

        var threadRequest = new JmapRequestBuilder();
        var threadGet = threadRequest.Add(JmapMethods.ThreadGet, new GetRequest(account.AccountId) { Ids = threadIds });
        var threads = (await jmap.SendAsync(threadRequest, cancellationToken).ConfigureAwait(false)).Bind(r => r.Get(threadGet));
        if (!threads.IsSuccess)
        {
            return threads.Error;
        }

        // Explicit ids (not a back-reference), so the client chunks a long thread over maxObjectsInGet.
        var emailIds = threads.Value.List.SelectMany(static t => t.EmailIds ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (emailIds.Count == 0)
        {
            return Page<MailMessage>.Empty;
        }

        var emailRequest = new JmapRequestBuilder();
        var emailGet = emailRequest.Add(JmapMethods.EmailGet, EmailProperties.ListRequest(account.AccountId, emailIds));
        var emails = (await jmap.SendAsync(emailRequest, cancellationToken).ConfigureAwait(false)).Bind(r => r.Get(emailGet));
        if (!emails.IsSuccess)
        {
            return emails.Error;
        }

        var messages = emails.Value.List
            .Where(e => !plan.ExcludedFolderIds.Any(excluded => EmailMapper.IsIn(e, excluded)))
            .OrderByDescending(static e => e.ReceivedAt)
            .Take(top)
            .Select(e => EmailMapper.ToMessage(e, folderId))
            .ToList();
        return new Page<MailMessage>(messages);
    }

    private async Task<Result<(JmapAccount Account, Email Email)>> FetchAsync(
        MailboxRef mailbox, MessageId id, Func<string, string, EmailGetRequest> build, CancellationToken cancellationToken)
    {
        var account = await jmap.ResolveAccountAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!account.IsSuccess)
        {
            return account.Error;
        }

        var jmapId = await locator.ResolveAsync(account.Value, id, cancellationToken).ConfigureAwait(false);
        if (!jmapId.IsSuccess)
        {
            return jmapId.Error;
        }

        var request = new JmapRequestBuilder();
        var get = request.Add(JmapMethods.EmailGet, build(account.Value.AccountId, jmapId.Value));
        var emails = (await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false)).Bind(r => r.Get(get));
        if (!emails.IsSuccess)
        {
            return emails.Error;
        }

        return emails.Value.List is [var email, ..] ? (account.Value, email) : MessageNotFound();
    }

    private async Task<Result<(JmapAccount Account, Email Email, EmailBodyPart Part)>> FindPartAsync(
        MailboxRef mailbox, MessageId id, AttachmentId attachment, CancellationToken cancellationToken)
    {
        var parsed = AttachmentItemId.Parse(attachment);
        if (!parsed.IsSuccess)
        {
            return parsed.Error;
        }

        var found = await FetchAsync(mailbox, id, AttachmentsRequest, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            return found.Error;
        }

        var (account, email) = found.Value;
        var part = parsed.Value.Find(email.BodyStructure);
        return part?.BlobId is null || part.SubParts is { Count: > 0 }
            ? Error.NotFound("The specified attachment was not found.", "attachmentId")
            : (account, email, part);
    }

    private Task<Result<BlobContent>> DownloadAsync(JmapAccount account, EmailBodyPart part, CancellationToken cancellationToken)
    {
        var attachment = MimeParts.ToAttachment(part, default, null);
        var type = attachment.Kind == AttachmentKind.Item ? RawMessageType : attachment.ContentType;
        return blobs.DownloadAsync(account.AccountId, part.BlobId!, attachment.Name, type, cancellationToken);
    }

    private static EmailGetRequest AttachmentsRequest(string accountId, string jmapId) => new(accountId)
    {
        Ids = [jmapId],
        Properties = AttachmentProperties,
        BodyProperties = EmailProperties.BodyParts,
    };

    /// <summary>Email/get does not promise the order of <c>ids</c>; the query's order is the answer's.</summary>
    private static IEnumerable<Email> InOrder(IReadOnlyList<string> ids, IReadOnlyList<Email> emails)
    {
        var byId = emails.Where(static e => e.Id is not null).ToDictionary(static e => e.Id!, StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (byId.TryGetValue(id, out var email))
            {
                yield return email;
            }
        }
    }

    private static Error MessageNotFound() => Error.NotFound("The specified object was not found in the store.", "id");
}
