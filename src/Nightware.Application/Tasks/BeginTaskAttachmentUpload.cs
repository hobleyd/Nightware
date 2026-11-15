using Nightware.Domain.Kernel;
using Nightware.Domain.Tasks;

namespace Nightware.Application.Tasks;

/// <summary>Opens an upload session for a large attachment (<c>POST …/attachments/createUploadSession</c>).</summary>
public sealed class BeginTaskAttachmentUpload(ITaskAttachmentStore attachments)
{
    public async Task<Result<UploadSession>> ExecuteAsync(
        MailboxRef mailbox, TaskListId list, TaskId task, string name, string? contentType, long size, CancellationToken cancellationToken)
    {
        if (TaskAttachmentLimits.Validate(name, size) is { } invalid)
        {
            return invalid;
        }

        var type = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType.Trim();
        return await attachments.BeginUploadAsync(mailbox, list, task, name, type, size, cancellationToken).ConfigureAwait(false);
    }
}
