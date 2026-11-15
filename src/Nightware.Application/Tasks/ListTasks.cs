using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;
using Nightware.Domain.Tasks;

namespace Nightware.Application.Tasks;

/// <summary>The tasks of one list (<c>GET todo/lists/{id}/tasks</c>), filtered, ordered and paged by <see cref="ItemQuery"/>.</summary>
public sealed class ListTasks(ITaskStore tasks)
{
    public Task<Result<Page<TodoTask>>> ExecuteAsync(MailboxRef mailbox, TaskListId list, ItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return tasks.ListAsync(mailbox, list, query, cancellationToken);
    }
}
