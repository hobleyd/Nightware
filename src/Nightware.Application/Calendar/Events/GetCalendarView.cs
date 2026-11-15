using Nightware.Application.Common.Query;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Calendar.Events;

/// <summary>
/// Graph <c>GET calendarView</c>: the occurrences overlapping a window, recurrences expanded. The window
/// is bounded by <see cref="CalendarViewLimits.MaxWindow"/>.
/// </summary>
public sealed class GetCalendarView(ICalendarReader reader)
{
    public async Task<Result<Page<CalendarEvent>>> ExecuteAsync(MailboxRef mailbox, DateTimeOffset start, DateTimeOffset end, ItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (end <= start)
        {
            return Error.Validation("endDateTime must be later than startDateTime.", "endDateTime");
        }

        if (CalendarViewLimits.Check(start, end) is { } tooLong)
        {
            return tooLong;
        }

        return await reader.CalendarViewAsync(mailbox, start, end, query, cancellationToken).ConfigureAwait(false);
    }
}
