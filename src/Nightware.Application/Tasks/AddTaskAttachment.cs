using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;
using Nightware.Domain.Tasks;

namespace Nightware.Application.Tasks;

/// <summary>Attaches a small file sent inline (<c>POST …/tasks/{id}/attachments</c> with <c>contentBytes</c>).</summary>
public sealed class AddTaskAttachment(ITaskAttachmentStore attachments)
{
    public async Task<Result<TaskAttachment>> ExecuteAsync(MailboxRef mailbox, TaskListId list, TaskId task, NewAttachment attachment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        if (TaskAttachmentLimits.ValidateName(attachment.Name) is { } invalid)
        {
            return invalid;
        }

        if (attachment.Content.Length > TaskAttachmentLimits.MaxInlineBytes)
        {
            return new Error(
                ErrorKind.PayloadTooLarge,
                "requestEntityTooLarge",
                $"Attachments larger than {TaskAttachmentLimits.MaxInlineBytes} bytes must be uploaded with createUploadSession.");
        }

        return await attachments.CreateAsync(mailbox, list, task, attachment, cancellationToken).ConfigureAwait(false);
    }
}
