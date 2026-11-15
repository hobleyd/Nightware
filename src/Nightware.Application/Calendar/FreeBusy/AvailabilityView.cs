using System.Text;
using Nightware.Domain.Calendar;

namespace Nightware.Application.Calendar.FreeBusy;

/// <summary>
/// Builds Graph's <c>availabilityView</c> (ADR 0001 §23): one digit per interval of the window, each the
/// strongest status of the busy periods overlapping it (half-open intervals). The digits are Graph's
/// codes, but their order is not their strength — out of office outranks busy, which outranks tentative,
/// which outranks working elsewhere — so the combination uses an explicit precedence, not the larger digit.
/// </summary>
public static class AvailabilityView
{
    public static string Compute(IEnumerable<ScheduleItem> items, DateTimeOffset start, DateTimeOffset end, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "The interval must be positive.");
        }

        var slots = end <= start ? 0 : (int)Math.Ceiling((end - start) / interval);
        var view = new FreeBusyStatus[slots];
        foreach (var item in items.Where(i => Rank(i.Status) > 0 && i.End > i.Start))
        {
            var first = Math.Max(0, (int)Math.Floor((item.Start - start) / interval));
            var last = Math.Min(slots - 1, (int)Math.Ceiling((item.End - start) / interval) - 1);
            for (var slot = first; slot <= last; slot++)
            {
                if (Rank(item.Status) > Rank(view[slot]))
                {
                    view[slot] = item.Status;
                }
            }
        }

        var text = new StringBuilder(slots);
        foreach (var status in view)
        {
            text.Append((char)('0' + (int)status));
        }

        return text.ToString();
    }

    private static int Rank(FreeBusyStatus status) => status switch
    {
        FreeBusyStatus.Oof => 4,
        FreeBusyStatus.Busy => 3,
        FreeBusyStatus.Tentative => 2,
        FreeBusyStatus.WorkingElsewhere => 1,
        _ => 0, // free, and statuses the backend could not classify
    };
}
