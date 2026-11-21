using Nightware.Domain.Kernel;
using Nightware.Domain.Tasks;

namespace Nightware.Application.Tasks;

/// <summary>
/// <c>todo/lists/{id}</c> get, create, rename and delete (ADR 0006 D8). The default list is neither renamed
/// nor deleted, as To Do's is not; nor is a list shared with the caller.
/// </summary>
public sealed class TaskListsUseCase(ITaskListStore lists)
{
    public const string DefaultListName = "defaultList";

    public Task<Result<TaskList>> GetAsync(MailboxRef mailbox, TaskListId list, CancellationToken cancellationToken) =>
        lists.GetAsync(mailbox, list, cancellationToken);

    public Task<Result<TaskList>> CreateAsync(MailboxRef mailbox, string? displayName, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(displayName)
            ? Task.FromResult<Result<TaskList>>(Error.Validation("displayName is required.", "displayName"))
            : lists.CreateAsync(mailbox, displayName.Trim(), cancellationToken);

    public async Task<Result<TaskList>> RenameAsync(MailboxRef mailbox, TaskListId list, string? displayName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return Error.Validation("displayName is required.", "displayName");
        }

        var refused = await RefusedAsync(mailbox, list, "renamed", cancellationToken).ConfigureAwait(false);
        return refused ?? await lists.RenameAsync(mailbox, list, displayName.Trim(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result> DeleteAsync(MailboxRef mailbox, TaskListId list, CancellationToken cancellationToken)
    {
        var refused = await RefusedAsync(mailbox, list, "deleted", cancellationToken).ConfigureAwait(false);
        return refused is null ? await lists.DeleteAsync(mailbox, list, cancellationToken).ConfigureAwait(false) : Result.Failure(refused);
    }

    private async Task<Error?> RefusedAsync(MailboxRef mailbox, TaskListId list, string verb, CancellationToken cancellationToken)
    {
        var found = await lists.GetAsync(mailbox, list, cancellationToken).ConfigureAwait(false);
        return !found.IsSuccess ? found.Error
            : found.Value.WellKnownListName == DefaultListName ? Error.Validation($"The default list cannot be {verb}.", "id")
            : !found.Value.IsOwner ? Error.Forbidden($"A list shared with you cannot be {verb}.")
            : null;
    }
}

/// <summary><c>GET todo/lists/{id}/tasks/{id}</c>.</summary>
public sealed class GetTask(ITaskStore tasks)
{
    public Task<Result<TodoTask>> ExecuteAsync(MailboxRef mailbox, TaskListId list, TaskId task, CancellationToken cancellationToken) =>
        tasks.GetAsync(mailbox, list, task, cancellationToken);
}

/// <summary><c>DELETE todo/lists/{id}/tasks/{id}</c>.</summary>
public sealed class DeleteTask(ITaskStore tasks)
{
    public Task<Result> ExecuteAsync(MailboxRef mailbox, TaskListId list, TaskId task, CancellationToken cancellationToken) =>
        tasks.DeleteAsync(mailbox, list, task, cancellationToken);
}

/// <summary><c>GET …/attachments/{id}</c>: the attachment with its bytes inline (<c>contentBytes</c>), as To Do answers.</summary>
public sealed class GetTaskAttachment(ITaskAttachmentStore attachments)
{
    public async Task<Result<(TaskAttachment Attachment, byte[] Content)>> ExecuteAsync(
        MailboxRef mailbox, TaskListId list, TaskId task, AttachmentId attachment, CancellationToken cancellationToken)
    {
        var all = await attachments.ListAsync(mailbox, list, task, cancellationToken).ConfigureAwait(false);
        if (!all.IsSuccess)
        {
            return all.Error;
        }

        var found = all.Value.FirstOrDefault(a => a.Id == attachment);
        if (found is null)
        {
            return Error.NotFound("The specified object was not found in the store.", "id");
        }

        var content = await attachments.GetContentAsync(mailbox, list, task, attachment, cancellationToken).ConfigureAwait(false);
        if (!content.IsSuccess)
        {
            return content.Error;
        }

        var stream = await content.Value.Open(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return (found, buffer.ToArray());
        }
    }
}

/// <summary><c>DELETE …/attachments/{id}</c>.</summary>
public sealed class DeleteTaskAttachment(ITaskAttachmentStore attachments)
{
    public Task<Result> ExecuteAsync(MailboxRef mailbox, TaskListId list, TaskId task, AttachmentId attachment, CancellationToken cancellationToken) =>
        attachments.DeleteAsync(mailbox, list, task, attachment, cancellationToken);
}
