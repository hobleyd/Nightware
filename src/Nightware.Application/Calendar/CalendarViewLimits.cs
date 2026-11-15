using Nightware.Domain.Kernel;

namespace Nightware.Application.Calendar;

/// <summary>
/// The bound on a recurrence-expanding calendar view. Enforced by <c>GetCalendarView</c> for the
/// client's windows and again by the calendar reader, because other callers (an invitation's
/// occurrence lookup) take their window from mail anyone can send.
/// </summary>
public static class CalendarViewLimits
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

    /// <summary>A validation error when [<paramref name="start"/>, <paramref name="end"/>) is longer than <see cref="MaxWindow"/>.</summary>
    public static Error? Check(DateTimeOffset start, DateTimeOffset end) =>
        end - start > MaxWindow
            ? Error.Validation($"The time window from startDateTime to endDateTime cannot be longer than {MaxWindow.TotalDays:0} days.", "endDateTime")
            : null;
}
