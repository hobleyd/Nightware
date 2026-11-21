using System.Text.Json.Nodes;
using Nightware.Adapters.Jmap.Core;
using Nightware.Adapters.Jmap.Core.Errors;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;
using Nightware.Application.Mail;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Mail.Write;

/// <summary>
/// Folder create, rename, move and delete over <c>Mailbox/set</c>. Folder ids are JMAP mailbox ids and
/// survive renames and moves (ADR 0001 §3); <c>msgfolderroot</c> stands for "no parent" (§40).
/// </summary>
/// <remarks>
/// Stalwart refuses a sibling with the same name, case-insensitively, as <c>alreadyExists</c> on create,
/// rename and move alike; that becomes Graph's 409 <c>ErrorFolderExists</c>, which NightMail treats as
/// "use the existing folder".
/// </remarks>
internal sealed partial class JmapMailFolderWriter(IJmapClient jmap) : IMailFolderWriter
{
    private const string NewFolder = "folder";

    public async Task<Result<MailFolder>> CreateAsync(MailboxRef mailbox, FolderRef? parent, string displayName, CancellationToken cancellationToken)
    {
        var listed = await ListAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!listed.IsSuccess)
        {
            return listed.Error;
        }

        var (accountId, mailboxes) = listed.Value;
        var target = parent is null ? MailboxTarget.Root : WriteMailboxes.Resolve(mailboxes, parent);
        if (!target.IsSuccess)
        {
            return target.Error;
        }

        var request = new JmapRequestBuilder();
        var set = request.Add(JmapMethods.MailboxSet, new MailboxSetRequest(accountId)
        {
            Create = new Dictionary<string, Mailbox>(StringComparer.Ordinal)
            {
                [NewFolder] = new Mailbox { Name = displayName, ParentId = target.Value.MailboxId },
            },
        });
        var after = request.Add(JmapMethods.MailboxGet, WriteMailboxes.GetAll(accountId));
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return response.Error;
        }

        var created = response.Value.Get(set).Bind(s => Outcome(s.NotCreated, NewFolder) ?? s.Created(NewFolder));
        if (!created.IsSuccess)
        {
            return created.Error;
        }

        return Folder(response.Value, after, created.Value.Id);
    }

    public Task<Result<MailFolder>> RenameAsync(MailboxRef mailbox, FolderId id, string displayName, CancellationToken cancellationToken) =>
        UpdateAsync(mailbox, id, new Patch().Set("name", JsonValue.Create(displayName)).Build(), cancellationToken);

    public async Task<Result<MailFolder>> MoveAsync(MailboxRef mailbox, FolderId id, FolderRef destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var listed = await ListAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!listed.IsSuccess)
        {
            return listed.Error;
        }

        var target = WriteMailboxes.Resolve(listed.Value.Mailboxes, destination);
        if (!target.IsSuccess)
        {
            return target.Error;
        }

        // A null parentId is the top level; Stalwart rejects a move into the folder's own subtree.
        var patch = new Patch().Set("parentId", target.Value.MailboxId is { } parent ? JsonValue.Create(parent) : null).Build();
        return await UpdateAsync(listed.Value.AccountId, listed.Value.Mailboxes, id, patch, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result> DeleteAsync(MailboxRef mailbox, FolderId id, CancellationToken cancellationToken)
    {
        var listed = await ListAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!listed.IsSuccess)
        {
            return listed.Error;
        }

        var (accountId, mailboxes) = listed.Value;
        var folder = Existing(mailboxes, id);
        if (!folder.IsSuccess)
        {
            return folder.Error;
        }

        // Graph deletes the folder with its subfolders and their mail. JMAP refuses to destroy a mailbox
        // that still has children, so the subtree goes first, deepest first, in the same call.
        IReadOnlyList<Mailbox> doomed = [.. WriteMailboxes.Descendants(mailboxes, folder.Value.Id!), folder.Value];
        if (doomed.FirstOrDefault(m => m.Role is not null) is { } special)
        {
            return Error.Forbidden($"The well-known folder '{special.Name}' cannot be deleted.");
        }

        var ids = doomed.Select(m => m.Id!).ToList();
        var request = new JmapRequestBuilder();
        var set = request.Add(JmapMethods.MailboxSet, new MailboxSetRequest(accountId) { Destroy = ids, OnDestroyRemoveEmails = true });
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Bind(r => r.Get(set)).Then(s => s.AllSucceeded());
    }

    private async Task<Result<MailFolder>> UpdateAsync(MailboxRef mailbox, FolderId id, PatchObject patch, CancellationToken cancellationToken)
    {
        var listed = await ListAsync(mailbox, cancellationToken).ConfigureAwait(false);
        return listed.IsSuccess
            ? await UpdateAsync(listed.Value.AccountId, listed.Value.Mailboxes, id, patch, cancellationToken).ConfigureAwait(false)
            : listed.Error;
    }

    private async Task<Result<MailFolder>> UpdateAsync(string accountId, IReadOnlyList<Mailbox> mailboxes, FolderId id, PatchObject patch, CancellationToken cancellationToken)
    {
        var folder = Existing(mailboxes, id);
        if (!folder.IsSuccess)
        {
            return folder.Error;
        }

        var mailboxId = folder.Value.Id!;
        var request = new JmapRequestBuilder();
        var set = request.Add(JmapMethods.MailboxSet, new MailboxSetRequest(accountId)
        {
            Update = new Dictionary<string, PatchObject>(StringComparer.Ordinal) { [mailboxId] = patch },
        });
        var after = request.Add(JmapMethods.MailboxGet, WriteMailboxes.GetAll(accountId));
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return response.Error;
        }

        var updated = response.Value.Get(set).Then(s => Outcome(s.NotUpdated, mailboxId) ?? s.Updated(mailboxId));
        return updated.IsSuccess ? Folder(response.Value, after, mailboxId) : updated.Error!;
    }

    private async Task<Result<(string AccountId, IReadOnlyList<Mailbox> Mailboxes)>> ListAsync(MailboxRef mailbox, CancellationToken cancellationToken)
    {
        var account = await jmap.ResolveAccountAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!account.IsSuccess)
        {
            return account.Error;
        }

        var request = new JmapRequestBuilder();
        var get = request.Add(JmapMethods.MailboxGet, WriteMailboxes.GetAll(account.Value.AccountId));
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Bind(r => r.Get(get)).Map(r => (account.Value.AccountId, r.List));
    }

    /// <summary>The mailbox <paramref name="id"/> names; the root and well-known roles cannot be renamed, moved or deleted.</summary>
    private static Result<Mailbox> Existing(IReadOnlyList<Mailbox> mailboxes, FolderId id)
    {
        if (MailboxRoles.IsRoot(new FolderRef.ById(id)))
        {
            return Error.Validation("The mailbox root cannot be changed.", "id");
        }

        if (WriteMailboxes.Find(mailboxes, id) is not { } folder)
        {
            return WriteMailboxes.NoSuchFolder();
        }

        return folder.Role is null ? folder : Error.Forbidden($"The well-known folder '{folder.Name}' cannot be changed.");
    }

    private static Result<MailFolder> Folder(JmapResponse response, JmapCall<GetResponse<Mailbox>> get, string? id)
    {
        var list = response.Get(get);
        if (!list.IsSuccess)
        {
            return list.Error;
        }

        return list.Value.List.FirstOrDefault(m => m.Id == id) is { } mailbox
            ? WriteMailboxes.ToFolder(mailbox, list.Value.List)
            : JmapErrors.Unexpected("The mail server did not return the folder it just wrote.");
    }

    /// <summary>A name clash as Graph reports it; any other failure as the core maps it; null when there was none.</summary>
    private static Error? Outcome(Dictionary<string, SetError>? failures, string key)
    {
        if (failures?.TryGetValue(key, out var failure) != true)
        {
            return null;
        }

        return string.Equals(failure!.Type, "alreadyExists", StringComparison.Ordinal)
            ? new Error(ErrorKind.Conflict, "ErrorFolderExists", "A folder with the specified name already exists.", "displayName")
            : JmapErrors.FromSetError(failure, key);
    }
}
