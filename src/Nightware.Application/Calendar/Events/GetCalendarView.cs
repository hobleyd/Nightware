using Nightware.Application.Common.Query;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Calendar.Events;

/// <summary>Graph <c>GET calendarView</c>: the occurrences overlapping a window, recurrences expanded.</summary>
public sealed class GetCalendarView(ICalendarReader reader)
{
    /// <summary>
    /// The longest window served. Every occurrence in the window is expanded, materialised and sorted
    /// before a page is cut, so the span is what bounds the work: without it, <c>1900..2900</c> with one
    /// weekly series is ~52,000 instances per request.
    /// </summary>
    /// <remarks>
    /// NightMail's windows (2026-11): offline cache sync 28 days, reminders and commitments 14 days,
    /// month grid 42 days, scheduling pane about a day, invitation lookups a few hours. The one long
    /// window is the out-of-office meeting sweep, which uses the out-of-office dates the user picked;
    /// its pickers reach a year back and five forward (about six years), so ten years leaves a wide
    /// margin. The sweep runs after the out-of-office setting is saved and only shows an error when
    /// the view fails, so even a longer pick loses nothing.
    /// </remarks>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(3653);

    public async Task<Result<Page<CalendarEvent>>> ExecuteAsync(MailboxRef mailbox, DateTimeOffset start, DateTimeOffset end, ItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (end <= start)
        {
            return Error.Validation("endDateTime must be later than startDateTime.", "endDateTime");
        }

        if (end - start > MaxWindow)
        {
            return Error.Validation($"The time window from startDateTime to endDateTime cannot be longer than {MaxWindow.TotalDays:0} days.", "endDateTime");
        }

        return await reader.CalendarViewAsync(mailbox, start, end, query, cancellationToken).ConfigureAwait(false);
    }
}
