using System.Buffers.Text;
using System.Text.Json;

using Nightware.Adapters.Dav.Calendar;
using Nightware.Adapters.Dav.Core;
using Nightware.Application.Tasks;
using Nightware.Domain.Kernel;
using Nightware.Domain.Tasks;

namespace Nightware.Adapters.Dav.Tasks;

/// <summary>
/// <c>todo/lists/{id}/tasks/delta</c> over CalDAV <c>sync-collection</c> (ADR 0006 D8). The initial sync (one
/// unlimited sync-collection: Sabre refuses a limited one with 507) is every task, answered as a first page as
/// To Do does. After that, changed tasks are upserts; a changed checklist item stands for its task, which is
/// upserted instead; removed resources are <c>@removed</c>. The token names its list.
/// </summary>
internal sealed class DavTaskDelta(DavTaskCollections collections, DavTaskStore tasks, IDavClient dav, DavPaths paths) : ITaskDelta
{
    public async Task<Result<DeltaPage<TodoTask>>> DeltaAsync(MailboxRef mailbox, TaskListId list, DeltaToken? token, CancellationToken cancellationToken)
    {
        var listHref = await collections.ResolveListAsync(mailbox, list, cancellationToken).ConfigureAwait(false);
        if (!listHref.IsSuccess)
        {
            return listHref.Error;
        }

        string? syncToken = null;
        if (token is { } t)
        {
            var cursor = Decode(t);
            if (cursor is null || cursor.Value.List != list.Value)
            {
                return Error.SyncStateGone("The delta token is invalid or has expired; start again without one.");
            }

            syncToken = cursor.Value.SyncToken;
        }

        var sync = await dav.SyncCollectionAsync(listHref.Value, syncToken, [DavNames.GetETag], null, cancellationToken).ConfigureAwait(false);
        if (!sync.IsSuccess)
        {
            return sync.Error;
        }

        var entries = new List<DeltaEntry<TodoTask>>();
        var changed = sync.Value.Changed.Where(static r => r.IsFound).Select(r => paths.ToRelative(r.Href)).OfType<string>().ToHashSet(StringComparer.Ordinal);
        changed.Remove(listHref.Value);
        if (changed.Count > 0)
        {
            var all = await tasks.ReadAllAsync(listHref.Value, cancellationToken).ConfigureAwait(false);
            if (!all.IsSuccess)
            {
                return all.Error;
            }

            var visible = DavTaskStore.WithoutChecklistItems(all.Value).ToList();
            var byUid = visible.Where(v => v.Todo.Uid is not null).ToDictionary(v => v.Todo.Uid!, v => v.Href, StringComparer.Ordinal);
            var affected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (href, todo) in all.Value.Where(a => changed.Contains(a.Href)))
            {
                affected.Add(todo.ParentUid is { } parent && byUid.TryGetValue(parent, out var parentHref) ? parentHref : href);
            }

            entries.AddRange(visible.Where(v => affected.Contains(v.Href)).OrderBy(v => v.Href, StringComparer.Ordinal)
                .Select(static v => (DeltaEntry<TodoTask>)new DeltaEntry<TodoTask>.Upsert(v.Todo.Task)));
        }

        entries.AddRange(sync.Value.Removed.Select(paths.ToRelative).OfType<string>()
            .Select(static href => new DeltaEntry<TodoTask>.Removed(DavItemId.Resource(href).ToTaskId().Value, RemovedReason.Deleted)));
        var next = Encode(sync.Value.SyncToken, list);
        return token is null || sync.Value.IsTruncated ? DeltaPage<TodoTask>.More(entries, next) : DeltaPage<TodoTask>.Done(entries, next);
    }

    private static DeltaToken Encode(string syncToken, TaskListId list) =>
        new(Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new[] { syncToken, list.Value }, DavCursorJsonContext.Default.StringArray)));

    private static (string SyncToken, string List)? Decode(DeltaToken token)
    {
        try
        {
            return JsonSerializer.Deserialize(Base64Url.DecodeFromChars(token.Value), DavCursorJsonContext.Default.StringArray) is [var sync, var list]
                ? (sync, list)
                : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }
}
