using Nightware.Adapters.Jmap.Core;
using Nightware.Adapters.Jmap.Core.Errors;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;
using Nightware.Adapters.Jmap.Core.Session;
using Nightware.Application.Mail;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Mail.Write;

/// <summary>
/// Graph <c>POST mailFolders/{id}/copy</c>: the subtree is created in one <c>Mailbox/set</c> (each child's
/// <c>parentId</c> a <c>#creationId</c> reference, verified on Stalwart), then each folder's mail is copied page by page
/// with <c>Email/import</c> of the original blob, as a message copy is (<see cref="JmapMailMessageWriter.CopyAsync"/>).
/// </summary>
/// <remarks>
/// A failure part-way through leaves what was copied so far (JMAP has no transaction across requests); the caller gets
/// the error and can delete the partial copy. Folders are copied as plain folders: a well-known folder's role is not
/// carried, so copying the Inbox gives an ordinary folder named "Inbox".
/// </remarks>
internal sealed partial class JmapMailFolderWriter
{
    /// <summary>Messages per <c>Email/get</c> + <c>Email/import</c> round.</summary>
    internal const int CopyPageSize = 50;

    private static readonly IReadOnlyList<string> CopiedProperties = ["id", "blobId", "keywords", "receivedAt"];

    public async Task<Result<MailFolder>> CopyAsync(MailboxRef mailbox, FolderId id, FolderRef destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var account = await jmap.ResolveAccountAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!account.IsSuccess)
        {
            return account.Error;
        }

        var accountId = account.Value.AccountId;
        var listed = await ListAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!listed.IsSuccess)
        {
            return listed.Error;
        }

        var mailboxes = listed.Value.Mailboxes;
        if (MailboxRoles.IsRoot(new FolderRef.ById(id)))
        {
            return Error.Validation("The mailbox root cannot be copied.", "id");
        }

        if (WriteMailboxes.Find(mailboxes, id) is not { Id: { } sourceId } source)
        {
            return WriteMailboxes.NoSuchFolder();
        }

        var target = WriteMailboxes.Resolve(mailboxes, destination);
        if (!target.IsSuccess)
        {
            return target.Error;
        }

        // Parents before children: the reverse of the deepest-first order Mailbox/set destroy wants.
        IReadOnlyList<Mailbox> tree = [source, .. WriteMailboxes.Descendants(mailboxes, sourceId).Reverse()];
        if (target.Value.MailboxId is { } targetId && tree.Any(m => m.Id == targetId))
        {
            return Error.Validation("A folder cannot be copied into itself or one of its subfolders.", "destinationId");
        }

        var created = await CreateTreeAsync(accountId, tree, target.Value.MailboxId, cancellationToken).ConfigureAwait(false);
        if (!created.IsSuccess)
        {
            return created.Error;
        }

        foreach (var (original, copy) in created.Value)
        {
            var copied = await CopyMailAsync(account.Value, original, copy, cancellationToken).ConfigureAwait(false);
            if (!copied.IsSuccess)
            {
                return copied.Error!;
            }
        }

        var request = new JmapRequestBuilder();
        var after = request.Add(JmapMethods.MailboxGet, WriteMailboxes.GetAll(accountId));
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.IsSuccess ? Folder(response.Value, after, created.Value[0].Copy) : response.Error;
    }

    /// <summary>The new mailboxes, in <paramref name="tree"/>'s order, paired with the originals they copy.</summary>
    private async Task<Result<IReadOnlyList<(string Original, string Copy)>>> CreateTreeAsync(
        string accountId, IReadOnlyList<Mailbox> tree, string? parentId, CancellationToken cancellationToken)
    {
        var keys = tree.Select((m, i) => (m.Id!, Key: $"f{i}")).ToDictionary(static p => p.Item1, static p => p.Key, StringComparer.Ordinal);
        var create = new Dictionary<string, Mailbox>(StringComparer.Ordinal);
        for (var i = 0; i < tree.Count; i++)
        {
            var parent = i == 0 ? parentId : "#" + keys[tree[i].ParentId!];
            create[$"f{i}"] = new Mailbox { Name = tree[i].Name, ParentId = parent };
        }

        var request = new JmapRequestBuilder();
        var set = request.Add(JmapMethods.MailboxSet, new MailboxSetRequest(accountId) { Create = create });
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var result = response.Bind(r => r.Get(set));
        if (!result.IsSuccess)
        {
            return result.Error;
        }

        if (Outcome(result.Value.NotCreated, "f0") is { } clash)
        {
            return clash;
        }

        var pairs = new List<(string, string)>(tree.Count);
        for (var i = 0; i < tree.Count; i++)
        {
            var made = result.Value.Created($"f{i}");
            if (!made.IsSuccess)
            {
                return made.Error;
            }

            if (made.Value.Id is not { } newId)
            {
                return JmapErrors.Unexpected("The mail server created a folder without an id.");
            }

            pairs.Add((tree[i].Id!, newId));
        }

        return pairs;
    }

    /// <summary>Every message in <paramref name="original"/>, imported into <paramref name="copy"/> with its keywords, date and metadata.</summary>
    private async Task<Result> CopyMailAsync(JmapAccount account, string original, string copy, CancellationToken cancellationToken)
    {
        // The source doesn't change while it is copied (the copies land elsewhere), so positions are stable.
        for (var position = 0; ; position += CopyPageSize)
        {
            var request = new JmapRequestBuilder();
            var query = request.Add(JmapMethods.EmailQuery, new EmailQueryRequest(account.AccountId)
            {
                Filter = new EmailFilter { InMailbox = original },
                Sort = [new Comparator("receivedAt")],
                Position = position,
                Limit = CopyPageSize,
            });
            var get = request.Add(
                account.SupportsMetadata ? JmapMethods.EmailGetWithMetadata : JmapMethods.EmailGet,
                new EmailGetRequest(account.AccountId) { Properties = account.SupportsMetadata ? [.. CopiedProperties, "metadata"] : CopiedProperties },
                query.Ref("ids", JmapPaths.Ids));
            var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var emails = response.Bind(r => r.Get(get));
            if (!emails.IsSuccess)
            {
                return emails.Error;
            }

            var page = emails.Value.List.Where(static e => e.BlobId is not null).ToList();
            if (page.Count > 0)
            {
                var imported = await ImportPageAsync(account, page, copy, cancellationToken).ConfigureAwait(false);
                if (!imported.IsSuccess)
                {
                    return imported;
                }
            }

            if (emails.Value.List.Count < CopyPageSize)
            {
                return Result.Success();
            }
        }
    }

    private async Task<Result> ImportPageAsync(JmapAccount account, List<Email> page, string mailboxId, CancellationToken cancellationToken)
    {
        var emails = new Dictionary<string, EmailImport>(StringComparer.Ordinal);
        for (var i = 0; i < page.Count; i++)
        {
            emails[$"m{i}"] = new EmailImport(page[i].BlobId!, new Dictionary<string, bool>(StringComparer.Ordinal) { [mailboxId] = true })
            {
                Keywords = page[i].Keywords,
                ReceivedAt = page[i].ReceivedAt,
            };
        }

        var request = new JmapRequestBuilder();
        var import = request.Add(JmapMethods.EmailImport, new EmailImportRequest(account.AccountId, emails));
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var result = response.Bind(r => r.Get(import));
        if (!result.IsSuccess)
        {
            return result.Error;
        }

        // Email/import drops metadata (phase 12 Step 0): carry each original's in one Email/set.
        var metadata = new Dictionary<string, PatchObject>(StringComparer.Ordinal);
        for (var i = 0; i < page.Count; i++)
        {
            var created = result.Value.Created($"m{i}");
            if (!created.IsSuccess)
            {
                return created.Error;
            }

            if (account.SupportsMetadata && created.Value.Id is { } newId && MessageMetadata.Read(page[i].Metadata).ToJson() is { } json)
            {
                metadata[newId] = new Patch().Set($"metadata/{MessageMetadata.Namespace}", json).Build();
            }
        }

        if (metadata.Count == 0)
        {
            return Result.Success();
        }

        var carry = new JmapRequestBuilder();
        var set = carry.Add(JmapMethods.EmailSetWithMetadata, new SetRequest<Email>(account.AccountId) { Update = metadata });
        var carried = await jmap.SendAsync(carry, cancellationToken).ConfigureAwait(false);
        return carried.Bind(r => r.Get(set)).Then(static s => s.AllSucceeded());
    }
}
