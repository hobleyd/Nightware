using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Tasks.ICal;
using Nightware.Application.Tasks;
using Nightware.Domain.Kernel;
using Nightware.Domain.Tasks;
using TaskStatus = Nightware.Domain.Tasks.TaskStatus;

namespace Nightware.Adapters.Dav.Tasks;

/// <summary>
/// <see cref="IChecklistStore"/> over subtasks (ADR 0006 D8): an item is a VTODO in the same list whose
/// <c>RELATED-TO</c> names the task, as Nextcloud Tasks writes one. Its id is its resource's <see cref="DavItemId"/>.
/// Checking it off is <c>STATUS:COMPLETED</c> with COMPLETED and PERCENT-COMPLETE:100.
/// </summary>
internal sealed class DavChecklistStore(DavTaskCollections collections, DavTaskStore tasks, IDavClient dav, VTodoCodec codec) : IChecklistStore
{
    public async Task<Result<IReadOnlyList<ChecklistItem>>> ListAsync(MailboxRef mailbox, TaskListId list, TaskId task, CancellationToken cancellationToken)
    {
        var parent = await ParentAsync(mailbox, list, task, cancellationToken).ConfigureAwait(false);
        if (!parent.IsSuccess)
        {
            return parent.Error;
        }

        var all = await tasks.ReadAllAsync(parent.Value.ListHref, cancellationToken).ConfigureAwait(false);
        if (!all.IsSuccess)
        {
            return all.Error;
        }

        IReadOnlyList<ChecklistItem> items = all.Value
            .Where(t => t.Todo.ParentUid == parent.Value.Uid)
            .Select(t => ToItem(t.Href, t.Todo))
            .OrderBy(i => i.CreatedDateTime ?? DateTimeOffset.MaxValue)
            .ThenBy(i => i.Id, StringComparer.Ordinal)
            .ToList();
        return Result<IReadOnlyList<ChecklistItem>>.Success(items);
    }

    public async Task<Result<ChecklistItem>> CreateAsync(MailboxRef mailbox, TaskListId list, TaskId task, string displayName, bool isChecked, CancellationToken cancellationToken)
    {
        var parent = await ParentAsync(mailbox, list, task, cancellationToken).ConfigureAwait(false);
        if (!parent.IsSuccess)
        {
            return parent.Error;
        }

        var uid = Guid.NewGuid().ToString("N");
        var href = $"{parent.Value.ListHref}{uid}.ics";
        var ics = codec.CreateChild(new TaskDraft { Title = displayName, Status = isChecked ? TaskStatus.Completed : TaskStatus.NotStarted }, uid, parent.Value.Uid);
        var put = await dav.PutAsync(href, ics, DavTaskStore.ICalendarContentType, DavPrecondition.CreateOnly, cancellationToken).ConfigureAwait(false);
        return put.IsSuccess ? Item(ics, href) : put.Error;
    }

    public async Task<Result<ChecklistItem>> UpdateAsync(
        MailboxRef mailbox, TaskListId list, TaskId task, string item, string? displayName, bool? isChecked, CancellationToken cancellationToken)
    {
        var child = await ChildAsync(mailbox, list, task, item, cancellationToken).ConfigureAwait(false);
        if (!child.IsSuccess)
        {
            return child.Error;
        }

        var patch = new TaskDraft { Title = displayName, Status = isChecked switch { true => TaskStatus.Completed, false => TaskStatus.NotStarted, null => null } };
        var edited = await tasks.EditAsync(child.Value, ics => codec.Apply(ics, patch), cancellationToken).ConfigureAwait(false);
        return edited.IsSuccess ? Item(edited.Value, child.Value) : edited.Error;
    }

    public async Task<Result> DeleteAsync(MailboxRef mailbox, TaskListId list, TaskId task, string item, CancellationToken cancellationToken)
    {
        var child = await ChildAsync(mailbox, list, task, item, cancellationToken).ConfigureAwait(false);
        return child.IsSuccess ? await dav.DeleteAsync(child.Value, DavPrecondition.None, cancellationToken).ConfigureAwait(false) : child.Error;
    }

    private async Task<Result<(string ListHref, string Uid)>> ParentAsync(MailboxRef mailbox, TaskListId list, TaskId task, CancellationToken cancellationToken)
    {
        var listHref = await collections.ResolveListAsync(mailbox, list, cancellationToken).ConfigureAwait(false);
        if (!listHref.IsSuccess)
        {
            return listHref.Error;
        }

        var href = DavTaskCollections.ResolveTask(listHref.Value, task);
        if (!href.IsSuccess)
        {
            return href.Error;
        }

        var resource = await dav.GetAsync(href.Value, cancellationToken).ConfigureAwait(false);
        if (!resource.IsSuccess)
        {
            return resource.Error;
        }

        var parsed = codec.Parse(resource.Value.Body, href.Value);
        return parsed is { IsSuccess: true, Value: [{ Uid: { } uid }, ..] } ? (listHref.Value, uid) : NotFound();
    }

    /// <summary>The item's href, provided it is a subtask of <paramref name="task"/> in the same list.</summary>
    private async Task<Result<string>> ChildAsync(MailboxRef mailbox, TaskListId list, TaskId task, string item, CancellationToken cancellationToken)
    {
        var parent = await ParentAsync(mailbox, list, task, cancellationToken).ConfigureAwait(false);
        if (!parent.IsSuccess)
        {
            return parent.Error;
        }

        var href = DavTaskCollections.ResolveTask(parent.Value.ListHref, new TaskId(item));
        if (!href.IsSuccess)
        {
            return NotFound();
        }

        var resource = await dav.GetAsync(href.Value, cancellationToken).ConfigureAwait(false);
        if (!resource.IsSuccess)
        {
            return resource.Error;
        }

        var parsed = codec.Parse(resource.Value.Body, href.Value);
        return parsed is { IsSuccess: true, Value: [var todo, ..] } && todo.ParentUid == parent.Value.Uid ? href.Value : NotFound();
    }

    private Result<ChecklistItem> Item(string ics, string href)
    {
        var parsed = codec.Parse(ics, href);
        return parsed is { IsSuccess: true, Value: [var todo, ..] } ? ToItem(href, todo) : NotFound();
    }

    private static ChecklistItem ToItem(string href, ParsedTodo todo)
    {
        var isChecked = todo.Task.Status == TaskStatus.Completed;
        return new ChecklistItem(DavItemId.Resource(href).ToString(), todo.Task.Title, isChecked)
        {
            CreatedDateTime = todo.Task.CreatedDateTime,
            CheckedDateTime = isChecked ? todo.CompletedAt : null,
        };
    }

    private static Error NotFound() => Error.NotFound("The specified object was not found in the store.", "id");
}
