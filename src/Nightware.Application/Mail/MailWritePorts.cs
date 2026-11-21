using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail;

public interface IMailMessageWriter
{
    // Writes return ids, not messages: use cases re-read through IMailMessageReader for response
    // bodies, so the JMAP Email → MailMessage mapping exists once (Phase 2), not per write port.

    Task<Result> PatchAsync(MailboxRef mailbox, MessageId id, MessagePatch patch, CancellationToken cancellationToken);

    /// <summary>Moves a message; the id is preserved.</summary>
    Task<Result<MessageId>> MoveAsync(MailboxRef mailbox, MessageId id, FolderRef destination, CancellationToken cancellationToken);

    /// <summary>
    /// Graph <c>POST messages/{id}/copy</c>: a distinct message in <paramref name="destination"/>, with its own
    /// id, carrying the original's read state, flag, categories and importance.
    /// </summary>
    Task<Result<MessageId>> CopyAsync(MailboxRef mailbox, MessageId id, FolderRef destination, CancellationToken cancellationToken);

    /// <summary>Graph <c>DELETE messages/{id}</c>: moves to Deleted Items (or destroys when already there).</summary>
    Task<Result> SoftDeleteAsync(MailboxRef mailbox, MessageId id, CancellationToken cancellationToken);

    Task<Result> PermanentDeleteAsync(MailboxRef mailbox, MessageId id, CancellationToken cancellationToken);
}

public interface IMailFolderWriter
{
    Task<Result<MailFolder>> CreateAsync(MailboxRef mailbox, FolderRef? parent, string displayName, CancellationToken cancellationToken);

    Task<Result<MailFolder>> RenameAsync(MailboxRef mailbox, FolderId id, string displayName, CancellationToken cancellationToken);

    Task<Result<MailFolder>> MoveAsync(MailboxRef mailbox, FolderId id, FolderRef destination, CancellationToken cancellationToken);

    /// <summary>
    /// Graph <c>POST mailFolders/{id}/copy</c>: a new folder under <paramref name="destination"/> with the same name, its
    /// subfolders and a copy of every message in them (each a distinct message, as <see cref="IMailMessageWriter.CopyAsync"/>).
    /// </summary>
    Task<Result<MailFolder>> CopyAsync(MailboxRef mailbox, FolderId id, FolderRef destination, CancellationToken cancellationToken);

    Task<Result> DeleteAsync(MailboxRef mailbox, FolderId id, CancellationToken cancellationToken);
}

/// <summary>Drafts. A draft's <see cref="MessageId"/> stays stable across every change (ADR 0001 §1).</summary>
public interface IDraftStore
{
    Task<Result<MessageId>> CreateAsync(MailboxRef mailbox, DraftContent content, CancellationToken cancellationToken);

    Task<Result<MessageId>> ReplaceAsync(MailboxRef mailbox, MessageId draftId, DraftContent content, CancellationToken cancellationToken);

    Task<Result<MailAttachment>> AddAttachmentAsync(MailboxRef mailbox, MessageId draftId, NewAttachment attachment, CancellationToken cancellationToken);

    Task<Result> RemoveAttachmentAsync(MailboxRef mailbox, MessageId draftId, AttachmentId attachment, CancellationToken cancellationToken);
}

public interface IMailSender
{
    Task<Result> SendDraftAsync(MailboxRef mailbox, MessageId draftId, CancellationToken cancellationToken);

    Task<Result> SendAsync(MailboxRef mailbox, DraftContent content, bool saveToSentItems, CancellationToken cancellationToken);
}

/// <summary>How an imported (migrated) message should land.</summary>
/// <param name="IsRead">From MAPI <c>PR_MESSAGE_FLAGS</c> (0x0E07).</param>
/// <param name="ReceivedAt">From MAPI <c>PR_MESSAGE_DELIVERY_TIME</c> (0x0E06).</param>
/// <param name="SentAt">From MAPI <c>PR_CLIENT_SUBMIT_TIME</c> (0x0039); becomes the <c>Date</c> header.</param>
public sealed record ImportOptions(bool IsRead, DateTimeOffset? ReceivedAt, DateTimeOffset? SentAt, bool IsDraft = false);

/// <summary>
/// Imports an existing message into a folder (Graph <c>POST mailFolders/{id}/messages</c> with MAPI
/// extended properties, used by NightMail's mailbox migration).
/// </summary>
public interface IMailImporter
{
    Task<Result<MessageId>> ImportAsync(MailboxRef mailbox, FolderRef folder, DraftContent content, ImportOptions options, CancellationToken cancellationToken);
}
