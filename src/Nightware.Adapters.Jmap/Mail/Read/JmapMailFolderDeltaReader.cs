using Nightware.Adapters.Jmap.Core;
using Nightware.Adapters.Jmap.Core.Errors;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;
using Nightware.Adapters.Jmap.Core.Session;
using Nightware.Application.Mail;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Mail.Read;

/// <summary>
/// The folder hierarchy's delta feed (Graph <c>mailFolders/delta</c>, ADR 0006 D5). The initial sync is every
/// mailbox in one page (a mailbox tree is small) with the Mailbox state read alongside; after that it follows
/// <c>Mailbox/changes</c>. Created and updated mailboxes are upserts (a rename, a move, a count change);
/// destroyed ones are <c>@removed: deleted</c>. <c>cannotCalculateChanges</c> is 410, as for messages.
/// </summary>
internal sealed class JmapMailFolderDeltaReader(IJmapClient jmap) : IMailFolderDeltaReader
{
    /// <summary>The cursor's mailbox slot: this feed covers every mailbox, never one.</summary>
    internal const string Hierarchy = "*folders";

    public async Task<Result<DeltaPage<MailFolder>>> DeltaAsync(MailboxRef mailbox, DeltaToken? token, DeltaRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var account = await jmap.ResolveAccountAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!account.IsSuccess)
        {
            return account.Error;
        }

        if (token is { } t)
        {
            var cursor = JmapDeltaTokenCodec.Decode(t, Hierarchy);
            return cursor.IsSuccess
                ? await ChangesAsync(account.Value, cursor.Value.State!, request.PageSize, cancellationToken).ConfigureAwait(false)
                : cursor.Error;
        }

        var all = await JmapMailboxes.LoadAsync(jmap, account.Value, cancellationToken).ConfigureAwait(false);
        if (!all.IsSuccess)
        {
            return all.Error;
        }

        if (string.IsNullOrEmpty(all.Value.State))
        {
            return JmapErrors.Unexpected("Mailbox/get returned no state.");
        }

        var entries = all.Value.InTreeOrder().Select(m => (DeltaEntry<MailFolder>)new DeltaEntry<MailFolder>.Upsert(all.Value.ToFolder(m))).ToList();
        return DeltaPage<MailFolder>.Done(entries, JmapDeltaTokenCodec.Encode(JmapDeltaCursor.Changes(Hierarchy, all.Value.State)));
    }

    private async Task<Result<DeltaPage<MailFolder>>> ChangesAsync(JmapAccount account, string sinceState, int pageSize, CancellationToken cancellationToken)
    {
        var request = new JmapRequestBuilder();
        var changes = request.Add(JmapMethods.MailboxChanges, new ChangesRequest(account.AccountId, sinceState) { MaxChanges = pageSize });
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var delta = response.Bind(r => r.Get(changes));
        if (!delta.IsSuccess)
        {
            return delta.Error;
        }

        // The whole list, not just the changed ids: a folder's child count depends on its neighbours.
        var all = await JmapMailboxes.LoadAsync(jmap, account, cancellationToken).ConfigureAwait(false);
        if (!all.IsSuccess)
        {
            return all.Error;
        }

        var changed = delta.Value.Created.Concat(delta.Value.Updated).ToHashSet(StringComparer.Ordinal);
        var entries = new List<DeltaEntry<MailFolder>>();
        entries.AddRange(all.Value.InTreeOrder()
            .Where(m => m.Id is not null && changed.Contains(m.Id))
            .Select(m => (DeltaEntry<MailFolder>)new DeltaEntry<MailFolder>.Upsert(all.Value.ToFolder(m))));
        entries.AddRange(delta.Value.Destroyed.Select(static id => (DeltaEntry<MailFolder>)new DeltaEntry<MailFolder>.Removed(id, RemovedReason.Deleted)));

        var next = JmapDeltaTokenCodec.Encode(JmapDeltaCursor.Changes(Hierarchy, delta.Value.NewState));
        return delta.Value.HasMoreChanges ? DeltaPage<MailFolder>.More(entries, next) : DeltaPage<MailFolder>.Done(entries, next);
    }
}
