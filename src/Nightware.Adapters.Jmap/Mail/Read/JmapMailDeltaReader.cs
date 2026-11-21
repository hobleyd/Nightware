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
/// newest first, each page anchored just after the last message of the one before (so a deletion
/// above the cursor can't shift a message past it unseen), having first captured the account's
/// Email state. If that message is gone (<c>anchorNotFound</c>) the page is read by position from a
/// page earlier instead: the overlap is repeated upserts, which the client merges. Once it is through,
/// the feed follows <c>Email/changes</c> from that state, so nothing that changed while the client
/// was paging is missed. A change is an upsert while the message is in the folder and
/// <c>@removed: deleted</c> when it is gone. <c>@removed: changed</c> (it left the folder) comes from
/// <c>Email/queryChanges</c> on the folder's query, so a change to a message filed elsewhere is not
/// reported at all. Without a query state (an older token, or the server could not calculate the
/// query's changes) a page falls back to reporting every changed message outside the folder as
/// having left it, and captures a query state for the next.
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
        var page = await InitialPageAsync(account, cursor, cursor.Anchor, cursor.Position ?? 0, pageSize, cancellationToken).ConfigureAwait(false);
        if (page.AnchorGone)
        {
            page = await InitialPageAsync(account, cursor, null, Math.Max(0, (cursor.Position ?? 0) - pageSize), pageSize, cancellationToken).ConfigureAwait(false);
        }

        return page.Result;
    }

    private async Task<(Result<DeltaPage<MailMessage>> Result, bool AnchorGone)> InitialPageAsync(
        JmapAccount account, JmapDeltaCursor cursor, string? anchor, int position, int pageSize, CancellationToken cancellationToken)
    {
        var request = new JmapRequestBuilder();

        // The state is read before the query, in the same request: anything that changes after it
        // is replayed by Email/changes, even if it also lands in a page of this initial walk. Sent in
        // two HTTP requests, a change between them would be in neither the walk nor the replay.
        var state = cursor.State is null
            ? request.RequireSingleRequest().Add(JmapMethods.EmailGet, new EmailGetRequest(account.AccountId) { Ids = [], Properties = ["id"] })
            : null;
        var query = request.Add(JmapMethods.EmailQuery, new EmailQueryRequest(account.AccountId)
        {
            Filter = FolderFilter(cursor.MailboxId),
            Sort = FolderSort,
            Position = anchor is null ? position : null,
            Anchor = anchor,
            AnchorOffset = anchor is null ? null : 1,
            Limit = pageSize,
            CalculateTotal = true,
        });
        var get = request.Add(EmailProperties.Get(account), EmailProperties.ListRequest(account), query.Ref("ids", JmapPaths.Ids));
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return (response.Error, false);
        }

        if (anchor is not null && response.Value.MethodErrorType(query) == "anchorNotFound")
        {
            return (default!, true);
        }

        var startState = cursor.State;
        var ids = response.Value.Get(query);
        var queryState = cursor.QueryState;
        if (state is not null)
        {
            var captured = response.Value.Get(state);
            if (!captured.IsSuccess)
            {
                return (captured.Error, false);
            }

            startState = captured.Value.State;
            queryState = ids.IsSuccess && ids.Value.CanCalculateChanges ? ids.Value.QueryState : null;
        }

        var emails = response.Value.Get(get);
        if (!ids.IsSuccess)
        {
            return (ids.Error, false);
        }

        if (!emails.IsSuccess)
        {
            return (emails.Error, false);
        }

        var entries = emails.Value.List
            .Where(e => e.Id is not null)
            .Select(e => (DeltaEntry<MailMessage>)new DeltaEntry<MailMessage>.Upsert(EmailMapper.ToMessage(e, cursor.MailboxId)))
            .ToList();
        // Where this page really started (the server resolves an anchor to a position), not where the cursor thought.
        var fetched = ids.Value.Ids.Count;
        var next = ids.Value.Position + fetched;
        var more = fetched > 0 && next < (ids.Value.Total ?? 0);
        if (string.IsNullOrEmpty(startState))
        {
            return (JmapErrors.Unexpected("Email/get returned no state."), false);
        }

        return (more
            ? DeltaPage<MailMessage>.More(entries, JmapDeltaTokenCodec.Encode(JmapDeltaCursor.Initial(cursor.MailboxId, startState, next, queryState, ids.Value.Ids[^1])))
            : DeltaPage<MailMessage>.Done(entries, JmapDeltaTokenCodec.Encode(JmapDeltaCursor.Changes(cursor.MailboxId, startState, queryState))), false);
    }

    private async Task<Result<DeltaPage<MailMessage>>> ChangesAsync(JmapAccount account, JmapDeltaCursor cursor, int pageSize, CancellationToken cancellationToken)
    {
        var request = new JmapRequestBuilder();

        // Who left the folder since the query state. Asked before Email/changes in the same request: a move
        // between the two is then in this page's Email/changes and in the next page's queryChanges, never in
        // neither. Without a state, capture one (an empty query) for the next page.
        var left = cursor.QueryState is { } since
            ? request.Add(JmapMethods.EmailQueryChanges, new QueryChangesRequest<EmailFilter>(account.AccountId, since) { Filter = FolderFilter(cursor.MailboxId), Sort = FolderSort })
            : null;
        var baseline = cursor.QueryState is null
            ? request.Add(JmapMethods.EmailQuery, new EmailQueryRequest(account.AccountId) { Filter = FolderFilter(cursor.MailboxId), Sort = FolderSort, Limit = 0 })
            : null;
        var changes = request.Add(JmapMethods.EmailChanges, new ChangesRequest(account.AccountId, cursor.State!) { MaxChanges = pageSize });
        var created = request.Add(EmailProperties.Get(account), EmailProperties.ListRequest(account), changes.Ref("ids", JmapPaths.Created));
        var updated = request.Add(EmailProperties.Get(account), EmailProperties.ListRequest(account), changes.Ref("ids", JmapPaths.Updated));
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

        // Null when the folder's departures are unknown this page (no state, or the server could not tell).
        var queryChanges = left is null ? null : response.Value.Get(left) is { IsSuccess: true } got ? got.Value : null;
        var nextQueryState = queryChanges?.NewQueryState
            ?? (baseline is not null && response.Value.Get(baseline) is { IsSuccess: true, Value.CanCalculateChanges: true } fresh ? fresh.Value.QueryState : null);

        var entries = new List<DeltaEntry<MailMessage>>();

        // A message created elsewhere was never in this folder: nothing to report.
        entries.AddRange(createdEmails.Value.List
            .Where(e => e.Id is not null && EmailMapper.IsIn(e, cursor.MailboxId))
            .Select(e => new DeltaEntry<MailMessage>.Upsert(EmailMapper.ToMessage(e, cursor.MailboxId))));

        foreach (var email in updatedEmails.Value.List.Where(static e => e.Id is not null))
        {
            if (EmailMapper.IsIn(email, cursor.MailboxId))
            {
                entries.Add(new DeltaEntry<MailMessage>.Upsert(EmailMapper.ToMessage(email, cursor.MailboxId)));
            }
            else if (queryChanges is null)
            {
                // Email/changes does not say where a message was before: without the query's changes, one
                // outside the folder now may have left it (the client ignores ids it does not hold).
                entries.Add(new DeltaEntry<MailMessage>.Removed(MailItemId.For(email).ToString(), RemovedReason.Changed));
            }
        }

        var destroyed = delta.Value.Destroyed.Where(MailItemId.IsValidJmapId).ToHashSet(StringComparer.Ordinal);
        if (queryChanges is not null)
        {
            // Out of the folder's query and not back in it, and not destroyed: it left the folder.
            var readded = queryChanges.Added.Select(static a => a.Id).ToHashSet(StringComparer.Ordinal);
            var reported = entries.OfType<DeltaEntry<MailMessage>.Upsert>().Select(static u => u.Item.Id.Value).ToHashSet(StringComparer.Ordinal);
            foreach (var id in queryChanges.Removed.Where(MailItemId.IsValidJmapId).Where(id => !readded.Contains(id) && !destroyed.Contains(id)))
            {
                var removedId = MailItemId.ForEmail(id).ToString();
                if (!reported.Contains(removedId))
                {
                    entries.Add(new DeltaEntry<MailMessage>.Removed(removedId, RemovedReason.Changed));
                }
            }
        }

        entries.AddRange(destroyed.Select(static id => new DeltaEntry<MailMessage>.Removed(MailItemId.ForEmail(id).ToString(), RemovedReason.Deleted)));

        // While Email/changes has more, keep the old query state (or none): its changes are then a superset of
        // every later page's. A state captured mid-way would miss a departure an unread page still holds.
        var queryState = delta.Value.HasMoreChanges ? cursor.QueryState : nextQueryState;
        var next = JmapDeltaTokenCodec.Encode(JmapDeltaCursor.Changes(cursor.MailboxId, delta.Value.NewState, queryState));
        return delta.Value.HasMoreChanges ? DeltaPage<MailMessage>.More(entries, next) : DeltaPage<MailMessage>.Done(entries, next);
    }

    private static EmailFilter FolderFilter(string mailboxId) => new() { InMailbox = mailboxId };

    private static readonly IReadOnlyList<Comparator> FolderSort = [new Comparator("receivedAt", IsAscending: false)];
}
