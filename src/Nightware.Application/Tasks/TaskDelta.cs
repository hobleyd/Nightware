using Nightware.Application.Common.Delta;
using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;
using Nightware.Domain.Tasks;

namespace Nightware.Application.Tasks;

/// <summary>Changes to the tasks of one list since a token (the adapter's own).</summary>
public interface ITaskDelta
{
    /// <param name="token">Null for the initial sync: every task in the list, and a token for what follows.</param>
    Task<Result<DeltaPage<TodoTask>>> DeltaAsync(MailboxRef mailbox, TaskListId list, DeltaToken? token, CancellationToken cancellationToken);
}

/// <summary>Graph <c>GET todo/lists/{id}/tasks/delta</c>.</summary>
public sealed class SyncTasks(ITaskDelta delta)
{
    public Task<Result<DeltaPage<TodoTask>>> ExecuteAsync(MailboxRef mailbox, TaskListId list, DeltaToken? token, CancellationToken cancellationToken) =>
        delta.DeltaAsync(mailbox, list, token, cancellationToken);
}

/// <summary>
/// Graph <c>GET todo/lists/delta</c>. A calendar home has no sync token for its collections, so the token is a
/// snapshot of the lists (<see cref="SnapshotDelta"/>); the initial sync answers with a next page first, as To Do does.
/// </summary>
public sealed class SyncTaskLists(ITaskListReader lists)
{
    private static readonly ItemQuery Everything = ItemQuery.Default with { Top = int.MaxValue };

    public async Task<Result<DeltaPage<TaskList>>> ExecuteAsync(MailboxRef mailbox, DeltaToken? token, CancellationToken cancellationToken)
    {
        var current = await lists.ListAsync(mailbox, Everything, cancellationToken).ConfigureAwait(false);
        return current.IsSuccess
            ? SnapshotDelta.Diff(current.Value.Items, token, l => l.Id.Value, l => $"{l.DisplayName}\u001F{l.IsShared}\u001F{l.WellKnownListName}", initialAsPage: true)
            : current.Error;
    }
}
