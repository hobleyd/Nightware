using Nightware.Application.Calendar.OnlineMeetings;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Calendar.Scheduling;

/// <summary>
/// Graph <c>POST events/{id}/cancel</c>: the organizer cancels the meeting (or one occurrence) and attendees are
/// told. A whole meeting's room is closed after it.
/// </summary>
public sealed class CancelEvent(ISchedulingActions scheduling, OnlineMeetingRooms rooms)
{
    public async Task<Result> ExecuteAsync(MailboxRef mailbox, EventId id, string? comment, CancellationToken cancellationToken)
    {
        var room = await rooms.RoomOfAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        var cancelled = await scheduling.CancelAsync(mailbox, id, string.IsNullOrEmpty(comment) ? null : comment, cancellationToken).ConfigureAwait(false);
        if (cancelled.IsSuccess)
        {
            await rooms.CloseAsync(room, cancellationToken).ConfigureAwait(false);
        }

        return cancelled;
    }
}
