using Nightware.Domain.Kernel;

namespace Nightware.Application.Calendar.Events;

/// <summary>Graph <c>DELETE events/{id}</c>; on an occurrence id, that occurrence only.</summary>
public sealed class DeleteEvent(ICalendarWriter writer)
{
    public Task<Result> ExecuteAsync(MailboxRef mailbox, EventId id, CancellationToken cancellationToken) =>
        writer.DeleteAsync(mailbox, id, cancellationToken);
}
