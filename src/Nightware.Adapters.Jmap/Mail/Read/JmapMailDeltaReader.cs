using Nightware.Adapters.Jmap.Core;
using Nightware.Adapters.Jmap.Core.Errors;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;
using Nightware.Adapters.Jmap.Core.Session;
using Nightware.Adapters.Jmap.Mail.Ids;
using Nightware.Application.Mail;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Mail.Read;

/// <summary>
/// The folder delta feed (ADR 0001 §6–8). The initial sync pages <c>Email/query inMailbox</c>
/// newest first by position, having first captured the account's Email state; once it is through,
/// the feed follows <c>Email/changes</c> from that state, so nothing that changed while the client
/// was paging is missed. A change is an upsert while the message is in the folder,
/// <c>@removed: changed</c> once it is not, and <c>@removed: deleted</c> when it is gone.
/// <c>cannotCalculateChanges</c> surfaces as <see cref="ErrorKind.SyncStateGone"/> (410).
/// </summary>
internal sealed class JmapMailDeltaReader(IJmapClient jmap) : IMailDeltaReader
{
    public async Task<Result<DeltaPage<MailMessage>>> DeltaAsync(MailboxRef mailbox, FolderRef folder, DeltaToken? token, DeltaRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(request);
        var account = await jmap.ResolveAccountAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!account.IsSuccess)
        {
            return account.Error;
        }

        var mailboxes = await JmapMailboxes.LoadAsync(jmap, account.Value, cancellationToken).ConfigureAwait(false);
        var resolved = mailboxes.Bind(m => m.Resolve(folder));
        if (!resolved.IsSuccess)
        {
            return resolved.Error;
        }

        var mailboxId = resolved.Value.Id!;
        var cursor = token is { } t ? JmapDeltaTokenCodec.Decode(t, mailboxId) : JmapDeltaCursor.Initial(mailboxId, null);
        if (!cursor.IsSuccess)
        {
            return cursor.Error;
        }

        // Ids feed Email/get by back-reference (not chunked), so a page stays within maxObjectsInGet.
        var pageSize = Math.Clamp(request.PageSize, 1, account.Value.Session.Limits.MaxObjectsInGet);
        return cursor.Value.Phase == DeltaPhase.Initial
            ? await InitialAsync(account.Value, cursor.Value, pageSize, cancellationToken).ConfigureAwait(false)
            : await ChangesAsync(account.Value, cursor.Value, pageSize, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<DeltaPage<MailMessage>>> InitialAsync(JmapAccount account, JmapDeltaCursor cursor, int pageSize, CancellationToken cancellationToken)
    {
        var position = cursor.Position ?? 0;
        var request = new JmapRequestBuilder();

        // The state is read before the query, in the same request: anything that changes after it
        // is replayed by Email/changes, even if it also lands in a page of this initial walk. Sent in
        // two HTTP requests, a change between them would be in neither the walk nor the replay.
        var state = cursor.State is null
            ? request.RequireSingleRequest().Add(JmapMethods.EmailGet, new EmailGetRequest(account.AccountId) { Ids = [], Properties = ["id"] })
            : null;
        var query = request.Add(JmapMethods.EmailQuery, new EmailQueryRequest(account.AccountId)
        {
            Filter = new EmailFilter { InMailbox = cursor.MailboxId },
            Sort = [new Comparator("receivedAt", IsAscending: false)],
            Position = position,
            Limit = pageSize,
            CalculateTotal = true,
        });
        var get = request.Add(JmapMethods.EmailGet, EmailProperties.ListRequest(account.AccountId), query.Ref("ids", JmapPaths.Ids));
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return response.Error;
        }

        var startState = cursor.State;
        if (state is not null)
        {
            var captured = response.Value.Get(state);
            if (!captured.IsSuccess)
            {
                return captured.Error;
            }

            startState = captured.Value.State;
        }

        var ids = response.Value.Get(query);
        var emails = response.Value.Get(get);
        if (!ids.IsSuccess)
        {
            return ids.Error;
        }

        if (!emails.IsSuccess)
        {
            return emails.Error;
        }

        var entries = emails.Value.List
            .Where(e => e.Id is not null)
            .Select(e => (DeltaEntry<MailMessage>)new DeltaEntry<MailMessage>.Upsert(EmailMapper.ToMessage(e, cursor.MailboxId)))
            .ToList();
        var fetched = ids.Value.Ids.Count;
        var more = fetched > 0 && position + fetched < (ids.Value.Total ?? 0);
        if (string.IsNullOrEmpty(startState))
        {
            return JmapErrors.Unexpected("Email/get returned no state.");
        }

        return more
            ? DeltaPage<MailMessage>.More(entries, JmapDeltaTokenCodec.Encode(JmapDeltaCursor.Initial(cursor.MailboxId, startState, position + fetched)))
            : DeltaPage<MailMessage>.Done(entries, JmapDeltaTokenCodec.Encode(JmapDeltaCursor.Changes(cursor.MailboxId, startState)));
    }

    private async Task<Result<DeltaPage<MailMessage>>> ChangesAsync(JmapAccount account, JmapDeltaCursor cursor, int pageSize, CancellationToken cancellationToken)
    {
        var request = new JmapRequestBuilder();
        var changes = request.Add(JmapMethods.EmailChanges, new ChangesRequest(account.AccountId, cursor.State!) { MaxChanges = pageSize });
        var created = request.Add(JmapMethods.EmailGet, EmailProperties.ListRequest(account.AccountId), changes.Ref("ids", JmapPaths.Created));
        var updated = request.Add(JmapMethods.EmailGet, EmailProperties.ListRequest(account.AccountId), changes.Ref("ids", JmapPaths.Updated));
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return response.Error;
        }

        var delta = response.Value.Get(changes);
        if (!delta.IsSuccess)
        {
            return delta.Error;
        }

        var createdEmails = response.Value.Get(created);
        var updatedEmails = response.Value.Get(updated);
        if (!createdEmails.IsSuccess)
        {
            return createdEmails.Error;
        }

        if (!updatedEmails.IsSuccess)
        {
            return updatedEmails.Error;
        }

        var entries = new List<DeltaEntry<MailMessage>>();

        // A message created elsewhere was never in this folder: nothing to report.
        entries.AddRange(createdEmails.Value.List
            .Where(e => e.Id is not null && EmailMapper.IsIn(e, cursor.MailboxId))
            .Select(e => new DeltaEntry<MailMessage>.Upsert(EmailMapper.ToMessage(e, cursor.MailboxId))));

        // Email/changes does not say where an updated message was before, so one that is not in
        // the folder now is reported as having left it (the client ignores ids it does not hold).
        foreach (var email in updatedEmails.Value.List.Where(static e => e.Id is not null))
        {
            entries.Add(EmailMapper.IsIn(email, cursor.MailboxId)
                ? new DeltaEntry<MailMessage>.Upsert(EmailMapper.ToMessage(email, cursor.MailboxId))
                : new DeltaEntry<MailMessage>.Removed(MailItemId.For(email).ToString(), RemovedReason.Changed));
        }

        entries.AddRange(delta.Value.Destroyed
            .Where(MailItemId.IsValidJmapId)
            .Select(static id => new DeltaEntry<MailMessage>.Removed(MailItemId.ForEmail(id).ToString(), RemovedReason.Deleted)));

        var next = JmapDeltaTokenCodec.Encode(JmapDeltaCursor.Changes(cursor.MailboxId, delta.Value.NewState));
        return delta.Value.HasMoreChanges ? DeltaPage<MailMessage>.More(entries, next) : DeltaPage<MailMessage>.Done(entries, next);
    }
}
