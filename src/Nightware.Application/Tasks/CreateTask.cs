using Nightware.Domain.Kernel;
using Nightware.Domain.Tasks;

namespace Nightware.Application.Tasks;

/// <summary>Adds a task to a list (<c>POST todo/lists/{id}/tasks</c>).</summary>
public sealed class CreateTask(ITaskStore tasks)
{
    public async Task<Result<TodoTask>> ExecuteAsync(MailboxRef mailbox, TaskListId list, TaskDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(draft.Title))
        {
            return Error.Validation("A task needs a title.", "title");
        }

        if (draft.ClearDueDateTime)
        {
            return Error.Validation("A new task has no due date to clear.", "dueDateTime");
        }

        return await tasks.CreateAsync(mailbox, list, draft, cancellationToken).ConfigureAwait(false);
    }
}
