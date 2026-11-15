using Nightware.Domain.Kernel;

namespace Nightware.Application.Calendar.Scheduling;

/// <summary>Graph <c>POST events/{id}/cancel</c>: the organizer cancels the meeting (or one occurrence) and attendees are told.</summary>
public sealed class CancelEvent(ISchedulingActions scheduling)
{
    public Task<Result> ExecuteAsync(MailboxRef mailbox, EventId id, string? comment, CancellationToken cancellationToken) =>
        scheduling.CancelAsync(mailbox, id, string.IsNullOrEmpty(comment) ? null : comment, cancellationToken);
}
