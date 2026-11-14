using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;
using Nightware.Domain.Tasks;

namespace Nightware.Application.Tasks;

public interface ITaskListReader
{
    Task<Result<Page<TaskList>>> ListAsync(MailboxRef mailbox, ItemQuery query, CancellationToken cancellationToken);
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
