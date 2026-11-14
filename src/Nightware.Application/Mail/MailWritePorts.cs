using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail;

public interface IMailMessageWriter
{
    Task<Result<MailMessage>> PatchAsync(MailboxRef mailbox, MessageId id, MessagePatch patch, CancellationToken cancellationToken);

    /// <summary>Moves a message; the id is preserved.</summary>
    Task<Result<MailMessage>> MoveAsync(MailboxRef mailbox, MessageId id, FolderRef destination, CancellationToken cancellationToken);

    /// <summary>Graph <c>DELETE messages/{id}</c>: moves to Deleted Items (or destroys when already there).</summary>
    Task<Result> SoftDeleteAsync(MailboxRef mailbox, MessageId id, CancellationToken cancellationToken);

    Task<Result> PermanentDeleteAsync(MailboxRef mailbox, MessageId id, CancellationToken cancellationToken);
}

public interface IMailFolderWriter
{
    Task<Result<MailFolder>> CreateAsync(MailboxRef mailbox, FolderRef? parent, string displayName, CancellationToken cancellationToken);

    Task<Result<MailFolder>> RenameAsync(MailboxRef mailbox, FolderId id, string displayName, CancellationToken cancellationToken);

    Task<Result<MailFolder>> MoveAsync(MailboxRef mailbox, FolderId id, FolderRef destination, CancellationToken cancellationToken);

    Task<Result> DeleteAsync(MailboxRef mailbox, FolderId id, CancellationToken cancellationToken);
}

/// <summary>Drafts. A draft's <see cref="MessageId"/> stays stable across every change (ADR 0001 §1).</summary>
public interface IDraftStore
{
    Task<Result<MailMessage>> CreateAsync(MailboxRef mailbox, DraftContent content, CancellationToken cancellationToken);

    Task<Result<MailMessage>> ReplaceAsync(MailboxRef mailbox, MessageId draftId, DraftContent content, CancellationToken cancellationToken);

    Task<Result<MailAttachment>> AddAttachmentAsync(MailboxRef mailbox, MessageId draftId, NewAttachment attachment, CancellationToken cancellationToken);

    Task<Result> RemoveAttachmentAsync(MailboxRef mailbox, MessageId draftId, AttachmentId attachment, CancellationToken cancellationToken);
}

public interface IMailSender
{
    Task<Result> SendDraftAsync(MailboxRef mailbox, MessageId draftId, CancellationToken cancellationToken);

    Task<Result> SendAsync(MailboxRef mailbox, DraftContent content, bool saveToSentItems, CancellationToken cancellationToken);
}
