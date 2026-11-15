using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Calendar.FreeBusy;

/// <summary>
/// Graph <c>POST calendar/getSchedule</c>: free/busy for people and rooms, with <c>availabilityView</c>
/// computed here from the busy periods the backend reports.
/// </summary>
public sealed class GetSchedule(IFreeBusyReader freeBusy)
{
    /// <summary>Graph's default <c>availabilityViewInterval</c>.</summary>
    public const int DefaultIntervalMinutes = 30;

    /// <summary>Graph accepts at most 100 schedules per request; NightMail sends batches of 20.</summary>
    public const int MaxSchedules = 100;

    /// <summary>Graph's limit on the time window.</summary>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(62);

    public async Task<Result<IReadOnlyList<ScheduleInformation>>> ExecuteAsync(MailboxRef mailbox, ScheduleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Validate() is { } invalid)
        {
            return invalid;
        }

        var start = CalendarRules.ToInstant(request.Start);
        var end = CalendarRules.ToInstant(request.End);
        var addresses = request.Schedules.Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var schedules = await freeBusy.GetAsync(mailbox, addresses, start, end, cancellationToken).ConfigureAwait(false);
        if (!schedules.IsSuccess)
        {
            return schedules.Error;
        }

        var interval = TimeSpan.FromMinutes(request.IntervalMinutes ?? DefaultIntervalMinutes);
        var byAddress = schedules.Value
            .GroupBy(s => s.ScheduleId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var result = new List<ScheduleInformation>(request.Schedules.Count);
        foreach (var requested in request.Schedules)
        {
            var id = requested.Trim();
            if (!byAddress.TryGetValue(id, out var schedule))
            {
                result.Add(new ScheduleInformation(requested, string.Empty, []) { ErrorMessage = "The free/busy information for this schedule could not be found." });
                continue;
            }

            if (schedule.ErrorMessage is { } error)
            {
                result.Add(new ScheduleInformation(requested, string.Empty, []) { ErrorMessage = error });
                continue;
            }

            var items = schedule.Items.Where(i => i.End > start && i.Start < end).OrderBy(i => i.Start).ToList();
            result.Add(new ScheduleInformation(requested, AvailabilityView.Compute(items, start, end, interval), items));
        }

        return result;
    }
}

/// <summary>The body of a <c>getSchedule</c> request.</summary>
/// <param name="IntervalMinutes">Graph <c>availabilityViewInterval</c> (5–1440); null for the 30-minute default.</param>
public sealed record ScheduleRequest(IReadOnlyList<string> Schedules, DateTimeTimeZone Start, DateTimeTimeZone End, int? IntervalMinutes = null)
{
    internal Error? Validate()
    {
        if (Schedules.Count == 0)
        {
            return Error.Validation("At least one schedule is required.", "schedules");
        }

        if (Schedules.Count > GetSchedule.MaxSchedules)
        {
            return Error.Validation($"At most {GetSchedule.MaxSchedules} schedules can be requested at once.", "schedules");
        }

        if (Schedules.FirstOrDefault(s => !CalendarRules.IsAddress(s?.Trim())) is { } bad)
        {
            return Error.Validation($"'{bad}' is not a valid email address.", "schedules");
        }

        if (IntervalMinutes is < 5 or > 1440)
        {
            return Error.Validation("availabilityViewInterval must be between 5 and 1440 minutes.", "availabilityViewInterval");
        }

        if (CalendarRules.Ordered(Start, End, "endTime", allowEmpty: false) is { } order)
        {
            return order;
        }

        return CalendarRules.ToInstant(End) - CalendarRules.ToInstant(Start) > GetSchedule.MaxWindow
            ? Error.Validation($"The time window cannot exceed {GetSchedule.MaxWindow.TotalDays} days.", "endTime")
            : null;
    }
}
