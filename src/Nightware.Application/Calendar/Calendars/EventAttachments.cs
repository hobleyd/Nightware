using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Calendar.Calendars;

/// <summary>An event's file attachments: the bytes in Nextcloud Files, the event referencing them (ADR 0006 D6).</summary>
public interface IEventAttachmentStore
{
    Task<Result<IReadOnlyList<EventAttachment>>> ListAsync(MailboxRef mailbox, EventId eventId, CancellationToken cancellationToken);

    Task<Result<EventAttachment>> GetAsync(MailboxRef mailbox, EventId eventId, AttachmentId id, CancellationToken cancellationToken);

    Task<Result<EventAttachment>> AddAsync(MailboxRef mailbox, EventId eventId, string name, string contentType, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);

    Task<Result> DeleteAsync(MailboxRef mailbox, EventId eventId, AttachmentId id, CancellationToken cancellationToken);
}

/// <summary>Graph <c>events/{id}/attachments</c>: list, get (with content), add a file, delete.</summary>
public sealed class EventAttachmentsUseCase(IEventAttachmentStore store)
{
    /// <summary>Graph's limit on an attachment added in one request.</summary>
    public const long MaxBytes = 3 * 1024 * 1024;

    public Task<Result<IReadOnlyList<EventAttachment>>> ListAsync(MailboxRef mailbox, EventId eventId, CancellationToken cancellationToken) =>
        store.ListAsync(mailbox, eventId, cancellationToken);

    public Task<Result<EventAttachment>> GetAsync(MailboxRef mailbox, EventId eventId, AttachmentId id, CancellationToken cancellationToken) =>
        store.GetAsync(mailbox, eventId, id, cancellationToken);

    public async Task<Result<EventAttachment>> AddAsync(MailboxRef mailbox, EventId eventId, string? name, string? contentType, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Error.Validation("An attachment needs a name.", "name");
        }

        if (content.Length > MaxBytes)
        {
            return new Error(ErrorKind.PayloadTooLarge, "requestEntityTooLarge", $"An attachment added in one request can be at most {MaxBytes} bytes.");
        }

        return await store.AddAsync(mailbox, eventId, name.Trim(), string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType, content, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<Result> DeleteAsync(MailboxRef mailbox, EventId eventId, AttachmentId id, CancellationToken cancellationToken) =>
        store.DeleteAsync(mailbox, eventId, id, cancellationToken);
}
