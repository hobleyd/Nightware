using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;
using Nightware.Domain.Tasks;

namespace Nightware.Application.Tasks;

public interface ITaskListReader
{
    Task<Result<Page<TaskList>>> ListAsync(MailboxRef mailbox, ItemQuery query, CancellationToken cancellationToken);
}

/// <summary>List management (<c>todo/lists/{id}</c>). The guards on the default list are the use case's.</summary>
public interface ITaskListStore
{
    Task<Result<TaskList>> GetAsync(MailboxRef mailbox, TaskListId list, CancellationToken cancellationToken);

    Task<Result<TaskList>> CreateAsync(MailboxRef mailbox, string displayName, CancellationToken cancellationToken);

    Task<Result<TaskList>> RenameAsync(MailboxRef mailbox, TaskListId list, string displayName, CancellationToken cancellationToken);

    Task<Result> DeleteAsync(MailboxRef mailbox, TaskListId list, CancellationToken cancellationToken);
}

public interface ITaskStore
{
    Task<Result<Page<TodoTask>>> ListAsync(MailboxRef mailbox, TaskListId list, ItemQuery query, CancellationToken cancellationToken);

    Task<Result<TodoTask>> GetAsync(MailboxRef mailbox, TaskListId list, TaskId id, CancellationToken cancellationToken);

    Task<Result<TodoTask>> CreateAsync(MailboxRef mailbox, TaskListId list, TaskDraft draft, CancellationToken cancellationToken);

    Task<Result<TodoTask>> UpdateAsync(MailboxRef mailbox, TaskListId list, TaskId id, TaskDraft patch, CancellationToken cancellationToken);

    Task<Result> DeleteAsync(MailboxRef mailbox, TaskListId list, TaskId id, CancellationToken cancellationToken);
}

public interface ITaskAttachmentStore
{
    Task<Result<IReadOnlyList<TaskAttachment>>> ListAsync(MailboxRef mailbox, TaskListId list, TaskId task, CancellationToken cancellationToken);

    Task<Result<BlobContent>> GetContentAsync(MailboxRef mailbox, TaskListId list, TaskId task, AttachmentId attachment, CancellationToken cancellationToken);

    Task<Result<TaskAttachment>> CreateAsync(MailboxRef mailbox, TaskListId list, TaskId task, NewAttachment attachment, CancellationToken cancellationToken);

    Task<Result> DeleteAsync(MailboxRef mailbox, TaskListId list, TaskId task, AttachmentId attachment, CancellationToken cancellationToken);

    Task<Result<UploadSession>> BeginUploadAsync(MailboxRef mailbox, TaskListId list, TaskId task, string name, string contentType, long size, CancellationToken cancellationToken);

    /// <param name="rangeStart">First byte offset of <paramref name="chunk"/> (from <c>Content-Range</c>).</param>
    Task<Result<UploadProgress>> PutChunkAsync(UploadSession session, long rangeStart, long rangeEnd, Stream chunk, CancellationToken cancellationToken);
}

/// <summary>A task's checklist (Graph <c>checklistItems</c>): its subtasks.</summary>
public interface IChecklistStore
{
    Task<Result<IReadOnlyList<ChecklistItem>>> ListAsync(MailboxRef mailbox, TaskListId list, TaskId task, CancellationToken cancellationToken);

    Task<Result<ChecklistItem>> CreateAsync(MailboxRef mailbox, TaskListId list, TaskId task, string displayName, bool isChecked, CancellationToken cancellationToken);

    /// <summary>Applies the non-null arguments.</summary>
    Task<Result<ChecklistItem>> UpdateAsync(MailboxRef mailbox, TaskListId list, TaskId task, string item, string? displayName, bool? isChecked, CancellationToken cancellationToken);

    Task<Result> DeleteAsync(MailboxRef mailbox, TaskListId list, TaskId task, string item, CancellationToken cancellationToken);
}

/// <summary>A task's links to items elsewhere (Graph <c>linkedResources</c>).</summary>
public interface ILinkedResourceStore
{
    Task<Result<IReadOnlyList<LinkedResource>>> ListAsync(MailboxRef mailbox, TaskListId list, TaskId task, CancellationToken cancellationToken);

    /// <param name="resource">Its <see cref="LinkedResource.Id"/> is ignored; the store mints one.</param>
    Task<Result<LinkedResource>> CreateAsync(MailboxRef mailbox, TaskListId list, TaskId task, LinkedResource resource, CancellationToken cancellationToken);

    /// <param name="patch">Its non-null members replace the stored ones.</param>
    Task<Result<LinkedResource>> UpdateAsync(MailboxRef mailbox, TaskListId list, TaskId task, string id, LinkedResource patch, CancellationToken cancellationToken);

    Task<Result> DeleteAsync(MailboxRef mailbox, TaskListId list, TaskId task, string id, CancellationToken cancellationToken);
}
