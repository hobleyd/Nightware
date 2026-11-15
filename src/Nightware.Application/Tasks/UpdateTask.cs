using Nightware.Domain.Kernel;
using Nightware.Domain.Tasks;

namespace Nightware.Application.Tasks;

/// <summary>Patches a task (<c>PATCH todo/lists/{id}/tasks/{id}</c>) and returns it as stored.</summary>
public sealed class UpdateTask(ITaskStore tasks)
{
    public async Task<Result<TodoTask>> ExecuteAsync(MailboxRef mailbox, TaskListId list, TaskId task, TaskDraft patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (patch.Title is { } title && string.IsNullOrWhiteSpace(title))
        {
            return Error.Validation("A task's title cannot be empty.", "title");
        }

        // Graph answers an empty PATCH with the unchanged task; writing it back would only bump LAST-MODIFIED.
        return patch == new TaskDraft()
            ? await tasks.GetAsync(mailbox, list, task, cancellationToken).ConfigureAwait(false)
            : await tasks.UpdateAsync(mailbox, list, task, patch, cancellationToken).ConfigureAwait(false);
    }
}
