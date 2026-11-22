using Nightware.Application.Calendar.OnlineMeetings;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Calendar.Events;

/// <summary>
/// Graph <c>DELETE events/{id}</c>; on an occurrence id, that occurrence only. When the organizer deletes the
/// whole meeting, its meeting room is closed after it.
/// </summary>
public sealed class DeleteEvent(ICalendarWriter writer, OnlineMeetingRooms rooms)
{
    public async Task<Result> ExecuteAsync(MailboxRef mailbox, EventId id, CancellationToken cancellationToken)
    {
        var room = await rooms.RoomOfAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        var deleted = await writer.DeleteAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        if (deleted.IsSuccess)
        {
            await rooms.CloseAsync(room, cancellationToken).ConfigureAwait(false);
        }

        return deleted;
    }
}
