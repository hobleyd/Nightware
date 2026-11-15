using Nightware.Domain.Kernel;
using Nightware.Domain.Tasks;

namespace Nightware.Application.Tasks;

/// <summary>A task's attachments (<c>GET …/tasks/{id}/attachments</c>).</summary>
public sealed class ListTaskAttachments(ITaskAttachmentStore attachments)
{
    public Task<Result<IReadOnlyList<TaskAttachment>>> ExecuteAsync(MailboxRef mailbox, TaskListId list, TaskId task, CancellationToken cancellationToken) =>
        attachments.ListAsync(mailbox, list, task, cancellationToken);
}
