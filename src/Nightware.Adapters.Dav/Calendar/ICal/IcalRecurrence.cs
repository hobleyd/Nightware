using Ical.Net;
using Ical.Net.DataTypes;
using Nightware.Application.Common.Time;
using Nightware.Domain.Calendar;
using DomainPattern = Nightware.Domain.Calendar.RecurrencePattern;
using DomainRange = Nightware.Domain.Calendar.RecurrenceRange;

namespace Nightware.Adapters.Dav.Calendar.ICal;

/// <summary>
/// RRULE ↔ Graph <see cref="PatternedRecurrence"/>. Covers what Graph can express:
/// DAILY, WEEKLY (BYDAY, WKST), MONTHLY by month-day or by relative weekday (both
/// <c>BYDAY=2TU</c> and <c>BYDAY=TU;BYSETPOS=2</c>), YEARLY likewise, with COUNT, UNTIL or no end.
/// Rules Graph cannot represent (BYHOUR, BYWEEKNO, BYYEARDAY, negative BYMONTHDAY, several
/// months, setpos other than 1–4/-1, sub-daily frequencies) map to null.
/// </summary>
internal static class IcalRecurrence
{
    private static readonly string[] IndexNames = ["first", "second", "third", "fourth"];

    public static PatternedRecurrence? ToDomain(RecurrenceRule rule, CalDateTime start, IcalZoneContext zones)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(zones);
        if (rule.BySecond.Count > 0 || rule.ByMinute.Count > 0 || rule.ByHour.Count > 0
            || rule.ByYearDay.Count > 0 || rule.ByWeekNo.Count > 0 || rule.ByMonth.Count > 1)
        {
            return null;
        }

        var interval = Math.Max(1, rule.Interval);
        var pattern = rule.Frequency switch
        {
            FrequencyType.Daily => Daily(rule, interval),
            FrequencyType.Weekly => Weekly(rule, interval, start),
            FrequencyType.Monthly => Monthly(rule, interval, start),
            FrequencyType.Yearly => Yearly(rule, interval, start),
            _ => null,
        };
        if (pattern is null)
        {
            return null;
        }

        var zone = zones.ZoneNameOf(start);
        var startDate = start.HasTime ? DateOnly.FromDateTime(zones.ToDateTimeTimeZone(start).DateTime) : start.Date;
        DomainRange range;
        if (rule.Count is { } count)
        {
            range = new DomainRange(RecurrenceRangeType.Numbered, startDate) { NumberOfOccurrences = count };
        }
        else if (rule.Until is { } until)
        {
            DateOnly endDate;
            if (!until.HasTime)
            {
                endDate = until.Date;
            }
            else
            {
                // UNTIL is usually UTC: take its date in the series' own zone, or the last day is lost.
                var instant = zones.ToInstant(until);
                endDate = DateOnly.FromDateTime(IcalZoneContext.ToWallClock(instant, zone ?? GraphTimeZone.Utc));
            }

            range = new DomainRange(RecurrenceRangeType.EndDate, startDate) { EndDate = endDate };
        }
        else
        {
            range = new DomainRange(RecurrenceRangeType.NoEnd, startDate);
        }

        return new PatternedRecurrence(
            pattern with { FirstDayOfWeek = rule.FirstDayOfWeek },
            range with { RecurrenceTimeZone = start.HasTime ? zone : null });
    }

    /// <param name="zoneName">The zone the series' DTSTART is written in (null or UTC for UTC/all-day).</param>
    public static RecurrenceRule ToIcal(PatternedRecurrence recurrence, bool isAllDay, TimeOnly startTime, string? zoneName)
    {
        ArgumentNullException.ThrowIfNull(recurrence);
        var pattern = recurrence.Pattern;
        var rule = new RecurrenceRule(FrequencyOf(pattern.Type), Math.Max(1, pattern.Interval));
        switch (pattern.Type)
        {
            case RecurrencePatternType.Weekly:
                rule.ByDay.AddRange(pattern.DaysOfWeek.Distinct().Select(d => new WeekDay(d)));
                rule.FirstDayOfWeek = pattern.FirstDayOfWeek;
                break;
            case RecurrencePatternType.AbsoluteMonthly:
                rule.ByMonthDay.Add(pattern.DayOfMonth);
                break;
            case RecurrencePatternType.RelativeMonthly:
                AddRelative(rule, pattern);
                break;
            case RecurrencePatternType.AbsoluteYearly:
                rule.ByMonth.Add(pattern.Month);
                rule.ByMonthDay.Add(pattern.DayOfMonth);
                break;
            case RecurrencePatternType.RelativeYearly:
                rule.ByMonth.Add(pattern.Month);
                AddRelative(rule, pattern);
                break;
            case RecurrencePatternType.Daily:
            default:
                break;
        }

        var range = recurrence.Range;
        switch (range.Type)
        {
            case RecurrenceRangeType.Numbered when range.NumberOfOccurrences > 0:
                rule.Count = range.NumberOfOccurrences;
                break;
            case RecurrenceRangeType.EndDate when range.EndDate is { } endDate:
                if (isAllDay)
                {
                    rule.Until = new CalDateTime(endDate);
                }
                else
                {
                    // Inclusive end date: the last occurrence starts on that day in the series' zone.
                    var lastStart = endDate.ToDateTime(startTime);
                    var instant = GraphTimeZone.ToInstant(lastStart, zoneName ?? GraphTimeZone.Utc);
                    rule.Until = new CalDateTime(instant.UtcDateTime, CalDateTime.UtcTzId, true);
                }

                break;
            default:
                break;
        }

        return rule;
    }

    private static DomainPattern? Daily(RecurrenceRule rule, int interval)
    {
        if (rule.ByDay.Count == 0)
        {
            return new DomainPattern(RecurrencePatternType.Daily, interval);
        }

        // "Every weekday" is sometimes written DAILY;BYDAY=MO,TU,WE,TH,FR.
        return interval == 1 && rule.ByDay.All(d => d.Offset is null)
            ? new DomainPattern(RecurrencePatternType.Weekly, 1) { DaysOfWeek = rule.ByDay.Select(d => d.DayOfWeek).Distinct().ToList() }
            : null;
    }

    private static DomainPattern? Weekly(RecurrenceRule rule, int interval, CalDateTime start)
    {
        if (rule.ByDay.Any(d => d.Offset is not null) || rule.ByMonthDay.Count > 0 || rule.BySetPosition.Count > 0)
        {
            return null;
        }

        var days = rule.ByDay.Count > 0
            ? rule.ByDay.Select(d => d.DayOfWeek).Distinct().ToList()
            : [start.DayOfWeek];
        return new DomainPattern(RecurrencePatternType.Weekly, interval) { DaysOfWeek = days };
    }

    private static DomainPattern? Monthly(RecurrenceRule rule, int interval, CalDateTime start)
    {
        var relative = Relative(rule);
        if (relative is { } r)
        {
            return r.Index is null ? null : new DomainPattern(RecurrencePatternType.RelativeMonthly, interval) { DaysOfWeek = r.Days, Index = r.Index };
        }

        var day = AbsoluteDay(rule, start);
        return day is null ? null : new DomainPattern(RecurrencePatternType.AbsoluteMonthly, interval) { DayOfMonth = day.Value };
    }

    private static DomainPattern? Yearly(RecurrenceRule rule, int interval, CalDateTime start)
    {
        var month = rule.ByMonth.Count == 1 ? rule.ByMonth[0] : start.Month;
        var relative = Relative(rule);
        if (relative is { } r)
        {
            return r.Index is null ? null : new DomainPattern(RecurrencePatternType.RelativeYearly, interval) { DaysOfWeek = r.Days, Index = r.Index, Month = month };
        }

        var day = AbsoluteDay(rule, start);
        return day is null ? null : new DomainPattern(RecurrencePatternType.AbsoluteYearly, interval) { DayOfMonth = day.Value, Month = month };
    }

    private static int? AbsoluteDay(RecurrenceRule rule, CalDateTime start)
    {
        if (rule.BySetPosition.Count > 0)
        {
            return null;
        }

        return rule.ByMonthDay.Count switch
        {
            0 => start.Day,
            1 when rule.ByMonthDay[0] > 0 => rule.ByMonthDay[0],
            _ => null,
        };
    }

    /// <summary>Relative weekday form; Index is null when it cannot be expressed in Graph.</summary>
    private static (List<DayOfWeek> Days, string? Index)? Relative(RecurrenceRule rule)
    {
        if (rule.ByDay.Count == 0 || rule.ByMonthDay.Count > 0)
        {
            return null;
        }

        var days = rule.ByDay.Select(d => d.DayOfWeek).Distinct().ToList();
        var offsets = rule.ByDay.Select(d => d.Offset).Distinct().ToList();
        int? position;
        if (offsets.Count == 1 && offsets[0] is { } offset)
        {
            position = rule.BySetPosition.Count == 0 ? offset : null;
        }
        else if (offsets.Count == 1 && rule.BySetPosition.Count == 1)
        {
            position = rule.BySetPosition[0];
        }
        else
        {
            position = null;
        }

        return (days, IndexName(position));
    }

    private static string? IndexName(int? position) => position switch
    {
        >= 1 and <= 4 => IndexNames[position.Value - 1],
        -1 => "last",
        _ => null,
    };

    private static int IndexPosition(string? index) => index?.ToUpperInvariant() switch
    {
        "SECOND" => 2,
        "THIRD" => 3,
        "FOURTH" => 4,
        "LAST" => -1,
        _ => 1,
    };

    private static void AddRelative(RecurrenceRule rule, DomainPattern pattern)
    {
        rule.ByDay.AddRange(pattern.DaysOfWeek.Distinct().Select(d => new WeekDay(d)));
        rule.BySetPosition.Add(IndexPosition(pattern.Index));
    }

    private static FrequencyType FrequencyOf(RecurrencePatternType type) => type switch
    {
        RecurrencePatternType.Daily => FrequencyType.Daily,
        RecurrencePatternType.Weekly => FrequencyType.Weekly,
        RecurrencePatternType.AbsoluteMonthly or RecurrencePatternType.RelativeMonthly => FrequencyType.Monthly,
        _ => FrequencyType.Yearly,
    };
}
