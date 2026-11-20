using System.Text.Json.Nodes;
using Nightware.Adapters.Jmap.Core;
using Nightware.Adapters.Jmap.Core.Errors;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;
using Nightware.Adapters.Jmap.Core.Session;
using Nightware.Adapters.Jmap.Mail.Ids;
using Nightware.Application.Mail;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Mail.Write;

/// <summary>
/// Keyword, metadata and mailbox changes to a message (<c>Email/set update</c>), its copy and its deletion.
/// JMAP ids are stable across moves, so a message keeps its Graph id (ADR 0001 §2).
/// </summary>
internal sealed class JmapMailMessageWriter(IJmapClient jmap, IJmapMessageLocator locator) : IMailMessageWriter
{
    private const string CopyCreationId = "copy";

    private static readonly IReadOnlyList<string> PlacementProperties = ["id", "keywords", "messageId", "mailboxIds"];
    private static readonly IReadOnlyList<string> CopyProperties = ["id", "blobId", "keywords", "messageId", "receivedAt"];

    public async Task<Result> PatchAsync(MailboxRef mailbox, MessageId id, MessagePatch patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var located = await jmap.LocateAsync(locator, mailbox, id, cancellationToken).ConfigureAwait(false);
        if (!located.IsSuccess)
        {
            return located.Error;
        }

        var keywords = KeywordPatch(patch);
        var metadata = located.Value.Account.SupportsMetadata && TouchesMetadata(patch);
        if (!metadata && NeedsMetadata(patch))
        {
            return Error.Unsupported("This mail server cannot keep a completed flag, flag dates, categories or importance on a message.");
        }

        if (!metadata)
        {
            return keywords.Count == 0 ? Result.Success() : await UpdateAsync(located.Value, keywords, cancellationToken).ConfigureAwait(false);
        }

        var stored = await MetadataAsync(located.Value, cancellationToken).ConfigureAwait(false);
        if (!stored.IsSuccess)
        {
            return stored.Error;
        }

        // A plain flag toggle or read-state change that leaves the stored state as it was writes the keywords
        // alone, exactly as before metadata existed (and so needs no right to write it, e.g. in a shared mailbox).
        var applied = stored.Value.Apply(patch);
        if (!NeedsMetadata(patch) && JsonNode.DeepEquals(applied.ToJson(), stored.Value.ToJson()))
        {
            return keywords.Count == 0 ? Result.Success() : await UpdateAsync(located.Value, keywords, cancellationToken).ConfigureAwait(false);
        }

        var written = WithMetadata(keywords, applied);
        return written.IsSuccess
            ? await UpdateAsync(located.Value, written.Value, cancellationToken, JmapMethods.EmailSetWithMetadata).ConfigureAwait(false)
            : written.Error;
    }

    public async Task<Result<MessageId>> CopyAsync(MailboxRef mailbox, MessageId id, FolderRef destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var located = await jmap.LocateAsync(locator, mailbox, id, cancellationToken).ConfigureAwait(false);
        if (!located.IsSuccess)
        {
            return located.Error;
        }

        var account = located.Value.Account;
        var request = new JmapRequestBuilder();
        var mailboxes = request.Add(JmapMethods.MailboxGet, WriteMailboxes.GetAll(account.AccountId));
        var get = request.Add(account.SupportsMetadata ? JmapMethods.EmailGetWithMetadata : JmapMethods.EmailGet, new EmailGetRequest(account.AccountId)
        {
            Ids = [located.Value.JmapId],
            Properties = account.SupportsMetadata ? [.. CopyProperties, "metadata"] : CopyProperties,
        });
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return response.Error;
        }

        var list = response.Value.Get(mailboxes);
        if (!list.IsSuccess)
        {
            return list.Error;
        }

        var original = response.Value.Get(get).Bind(WriteSupport.Single);
        if (!original.IsSuccess)
        {
            return original.Error;
        }

        var target = WriteMailboxes.Resolve(list.Value.List, destination);
        if (!target.IsSuccess)
        {
            return target.Error;
        }

        if (target.Value.IsRoot)
        {
            return Error.Validation("A message cannot be copied to the mailbox root.", "destinationId");
        }

        var email = original.Value;
        if (email.BlobId is null)
        {
            return JmapErrors.Unexpected("The mail server returned a message without its content.");
        }

        var copied = await ImportAsync(account, email, target.Value.MailboxId!, cancellationToken).ConfigureAwait(false);
        if (!copied.IsSuccess)
        {
            return copied.Error;
        }

        // Email/import drops metadata (Step 0), so the copy is given the original's in a second call.
        var metadata = MessageMetadata.Read(email.Metadata).ToJson();
        if (account.SupportsMetadata && metadata is not null)
        {
            var carried = await UpdateAsync(
                new LocatedMessage(account, copied.Value),
                new Patch().Set($"metadata/{MessageMetadata.Namespace}", metadata).Build(),
                cancellationToken,
                JmapMethods.EmailSetWithMetadata).ConfigureAwait(false);
            if (!carried.IsSuccess)
            {
                return carried.Error;
            }
        }

        return MailItemId.For(copied.Value, email.Keywords, email.MessageId).ToMessageId();
    }

    public async Task<Result<MessageId>> MoveAsync(MailboxRef mailbox, MessageId id, FolderRef destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var placed = await PlacementAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        if (!placed.IsSuccess)
        {
            return placed.Error;
        }

        var (located, email, mailboxes) = placed.Value;
        var target = WriteMailboxes.Resolve(mailboxes, destination);
        if (!target.IsSuccess)
        {
            return target.Error;
        }

        if (target.Value.IsRoot)
        {
            return Error.Validation("A message cannot be moved to the mailbox root.", "destinationId");
        }

        var moved = await UpdateAsync(located, MailboxPatch(target.Value.MailboxId!), cancellationToken).ConfigureAwait(false);
        return moved.IsSuccess
            ? MailItemId.For(located.JmapId, email.Keywords, email.MessageId).ToMessageId()
            : moved.Error!;
    }

    public async Task<Result> SoftDeleteAsync(MailboxRef mailbox, MessageId id, CancellationToken cancellationToken)
    {
        var placed = await PlacementAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        if (!placed.IsSuccess)
        {
            return placed.Error;
        }

        var (located, email, mailboxes) = placed.Value;
        var trash = WriteMailboxes.IdOfRole(mailboxes, MailboxRoles.Trash);
        if (trash is null)
        {
            return Error.NotFound("The mailbox has no Deleted Items folder.");
        }

        // Graph semantics: a delete moves to Deleted Items; deleting from there is final.
        return email.MailboxIds?.ContainsKey(trash) == true
            ? await DestroyAsync(located, cancellationToken).ConfigureAwait(false)
            : await UpdateAsync(located, MailboxPatch(trash), cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result> PermanentDeleteAsync(MailboxRef mailbox, MessageId id, CancellationToken cancellationToken)
    {
        var located = await jmap.LocateAsync(locator, mailbox, id, cancellationToken).ConfigureAwait(false);
        return located.IsSuccess
            ? await DestroyAsync(located.Value, cancellationToken).ConfigureAwait(false)
            : located.Error;
    }

    /// <summary>
    /// The keyword changes for a patch: <c>isRead</c> is <c>$seen</c>; a flag sets <c>$flagged</c> only while
    /// flagged, since a completed flag is no longer one to an IMAP client.
    /// </summary>
    internal static PatchObject KeywordPatch(MessagePatch patch)
    {
        var builder = new Patch();
        if (patch.IsRead is { } isRead)
        {
            builder.SetEntry("keywords", Keywords.Seen, isRead);
        }

        if (patch.Flag is { } flag)
        {
            builder.SetEntry("keywords", Keywords.Flagged, flag.Status == FlagStatus.Flagged);
        }

        return builder.Build();
    }

    /// <summary>The patch changes something kept in metadata (where the account has it).</summary>
    internal static bool TouchesMetadata(MessagePatch patch) =>
        patch.Flag is not null || patch.Categories is not null || patch.Importance is not null;

    /// <summary>The patch cannot be carried out with keywords alone.</summary>
    internal static bool NeedsMetadata(MessagePatch patch) =>
        patch.Flag is { } flag && (flag.Status == FlagStatus.Complete || flag.StartDateTime is not null || flag.DueDateTime is not null || flag.CompletedDateTime is not null)
        || patch.Categories is { Count: > 0 }
        || patch.Importance is { } importance && importance != Importance.Normal;

    /// <summary>
    /// <paramref name="keywords"/> plus the whole metadata namespace: Stalwart patches inside a namespace only
    /// once it exists, so it is always written whole (null removes it).
    /// </summary>
    internal static Result<PatchObject> WithMetadata(PatchObject keywords, MessageMetadata metadata)
    {
        var json = metadata.ToJson();
        if (json is not null && System.Text.Encoding.UTF8.GetByteCount(json.ToJsonString()) > MessageMetadata.MaxBytes)
        {
            return Error.Validation("The message's categories are too long to store.", "categories");
        }

        var patch = new PatchObject();
        foreach (var (key, value) in keywords)
        {
            patch[key] = value?.DeepClone();
        }

        patch[$"metadata/{MessageMetadata.Namespace}"] = json;
        return patch;
    }

    /// <summary>Replaces every mailbox of the message: Graph's move leaves it in exactly one folder.</summary>
    private static PatchObject MailboxPatch(string mailboxId) =>
        new Patch().Set("mailboxIds", new JsonObject { [mailboxId] = true }).Build();

    private async Task<Result<(LocatedMessage Located, Email Email, IReadOnlyList<Mailbox> Mailboxes)>> PlacementAsync(
        MailboxRef mailbox, MessageId id, CancellationToken cancellationToken)
    {
        var located = await jmap.LocateAsync(locator, mailbox, id, cancellationToken).ConfigureAwait(false);
        if (!located.IsSuccess)
        {
            return located.Error;
        }

        var request = new JmapRequestBuilder();
        var mailboxes = request.Add(JmapMethods.MailboxGet, WriteMailboxes.GetAll(located.Value.AccountId));
        var emails = request.Add(JmapMethods.EmailGet, new EmailGetRequest(located.Value.AccountId) { Ids = [located.Value.JmapId], Properties = PlacementProperties });
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return response.Error;
        }

        var list = response.Value.Get(mailboxes);
        if (!list.IsSuccess)
        {
            return list.Error;
        }

        return response.Value.Get(emails).Bind(WriteSupport.Single)
            .Map(email => (located.Value, email, list.Value.List));
    }

    private async Task<Result<MessageMetadata>> MetadataAsync(LocatedMessage located, CancellationToken cancellationToken)
    {
        var request = new JmapRequestBuilder();
        var get = request.Add(JmapMethods.EmailGetWithMetadata, new EmailGetRequest(located.AccountId) { Ids = [located.JmapId], Properties = ["id", "metadata"] });
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Bind(r => r.Get(get)).Bind(WriteSupport.Single).Map(static email => MessageMetadata.Read(email.Metadata));
    }

    private async Task<Result<string>> ImportAsync(JmapAccount account, Email original, string mailboxId, CancellationToken cancellationToken)
    {
        var request = new JmapRequestBuilder();
        var import = request.Add(JmapMethods.EmailImport, new EmailImportRequest(account.AccountId, new Dictionary<string, EmailImport>(StringComparer.Ordinal)
        {
            [CopyCreationId] = new(original.BlobId!, new Dictionary<string, bool>(StringComparer.Ordinal) { [mailboxId] = true })
            {
                Keywords = original.Keywords,
                ReceivedAt = original.ReceivedAt,
            },
        }));
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Bind(r => r.Get(import)).Bind(r => r.Created(CopyCreationId)).Bind<string>(email => email.Id is { } id
            ? id
            : JmapErrors.Unexpected("The mail server copied a message without an id."));
    }

    private async Task<Result> UpdateAsync(
        LocatedMessage located, PatchObject patch, CancellationToken cancellationToken, JmapMethod<SetRequest<Email>, SetResponse<Email>>? method = null)
    {
        var request = new JmapRequestBuilder();
        var set = request.Add(method ?? JmapMethods.EmailSet, new SetRequest<Email>(located.AccountId)
        {
            Update = new Dictionary<string, PatchObject>(StringComparer.Ordinal) { [located.JmapId] = patch },
        });
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Bind(r => r.Get(set)).Then(s => s.Updated(located.JmapId));
    }

    private async Task<Result> DestroyAsync(LocatedMessage located, CancellationToken cancellationToken)
    {
        var request = new JmapRequestBuilder();
        var set = request.Add(JmapMethods.EmailSet, new SetRequest<Email>(located.AccountId) { Destroy = [located.JmapId] });
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Bind(r => r.Get(set)).Then(s => s.Destroyed(located.JmapId));
    }
}
