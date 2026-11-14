using Nightware.Domain.Kernel;

namespace Nightware.Application.Tasks;

/// <summary>An attachment's bytes (<c>GET …/attachments/{id}/$value</c>).</summary>
public sealed class GetTaskAttachmentContent(ITaskAttachmentStore attachments)
{
    public Task<Result<BlobContent>> ExecuteAsync(MailboxRef mailbox, TaskListId list, TaskId task, AttachmentId attachment, CancellationToken cancellationToken) =>
        attachments.GetContentAsync(mailbox, list, task, attachment, cancellationToken);
}
