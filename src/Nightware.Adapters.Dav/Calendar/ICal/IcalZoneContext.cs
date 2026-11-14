using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;
using Nightware.Application.Common.Time;
using Nightware.Domain.Calendar;
using IcalCalendar = Ical.Net.Calendar;

namespace Nightware.Adapters.Dav.Calendar.ICal;

/// <summary>
/// Interprets the date-times of one parsed VCALENDAR. Ical.Net's own conversions are never
/// used: for a TZID it cannot resolve (e.g. Outlook's "(UTC+10:00) Canberra, Melbourne,
/// Sydney") <c>AsUtc</c> silently treats the wall clock as UTC. Resolution order:
/// <list type="number">
/// <item>the TZID as a Windows or IANA id (<see cref="GraphTimeZone"/>);</item>
/// <item>an IANA id embedded in a vendor-prefixed TZID (<c>/mozilla.org/…/Europe/Berlin</c>) or the VTIMEZONE's <c>X-LIC-LOCATION</c>;</item>
/// <item>the offsets of the embedded VTIMEZONE observances (the value is then reported in UTC);</item>
/// <item>UTC.</item>
/// </list>
/// Floating times are interpreted in <see cref="FloatingTimeZone"/>; all-day values are
/// reported as midnight with zone <c>UTC</c>.
/// </summary>
internal sealed class IcalZoneContext
{
    private readonly Dictionary<string, VTimeZone> _timeZones;
    private readonly Dictionary<string, string?> _resolved = new(StringComparer.Ordinal);

    public IcalZoneContext(IcalCalendar? calendar, string floatingTimeZone)
    {
        _timeZones = calendar?.TimeZones
            .Where(tz => !string.IsNullOrEmpty(tz.TzId))
            .GroupBy(tz => tz.TzId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal) ?? new Dictionary<string, VTimeZone>(StringComparer.Ordinal);
        FloatingTimeZone = floatingTimeZone;
    }

    public string FloatingTimeZone { get; }

    /// <summary>The zone name a value is expressed in, or null when it can only be represented in UTC.</summary>
    public string? ZoneNameOf(CalDateTime value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.HasTime || value.IsUtc)
        {
            return GraphTimeZone.Utc;
        }

        return value.IsFloating ? FloatingTimeZone : ResolveZoneName(value.TzId!);
    }

    public DateTimeTimeZone ToDateTimeTimeZone(CalDateTime value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.HasTime)
        {
            return new DateTimeTimeZone(value.Date.ToDateTime(TimeOnly.MinValue), GraphTimeZone.Utc);
        }

        var wall = DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified);
        if (value.IsUtc)
        {
            return new DateTimeTimeZone(wall, GraphTimeZone.Utc);
        }

        var zone = ZoneNameOf(value);
        return zone is not null
            ? new DateTimeTimeZone(wall, zone)
            : new DateTimeTimeZone(ToInstant(value).UtcDateTime, GraphTimeZone.Utc);
    }

    /// <summary>The instant a value denotes; all-day values are midnight UTC of their date.</summary>
    public DateTimeOffset ToInstant(CalDateTime value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.HasTime)
        {
            return new DateTimeOffset(value.Date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        }

        var wall = DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified);
        if (value.IsUtc)
        {
            return new DateTimeOffset(wall, TimeSpan.Zero);
        }

        if (value.IsFloating)
        {
            return GraphTimeZone.ToInstant(wall, FloatingTimeZone);
        }

        var zone = ResolveZoneName(value.TzId!);
        if (zone is not null)
        {
            return GraphTimeZone.ToInstant(wall, zone);
        }

        return _timeZones.TryGetValue(value.TzId!, out var vtimezone) && OffsetFromObservances(vtimezone, wall) is { } offset
            ? new DateTimeOffset(wall, offset)
            : new DateTimeOffset(wall, TimeSpan.Zero);
    }

    /// <summary>Wall-clock time of an instant in a zone (UTC for unknown zones).</summary>
    public static DateTime ToWallClock(DateTimeOffset instant, string? zoneName) =>
        GraphTimeZone.TryFind(zoneName, out var zone)
            ? GraphTimeZone.ToWallClock(instant, zone)
            : DateTime.SpecifyKind(instant.UtcDateTime, DateTimeKind.Unspecified);

    private string? ResolveZoneName(string tzid)
    {
        if (_resolved.TryGetValue(tzid, out var cached))
        {
            return cached;
        }

        var resolved = Resolve(tzid);
        _resolved[tzid] = resolved;
        return resolved;
    }

    private string? Resolve(string tzid)
    {
        if (GraphTimeZone.TryFind(tzid, out _))
        {
            return tzid;
        }

        // Vendor-prefixed ids: "/mozilla.org/20050126_1/Europe/Berlin", "/citadel.org/20190914_1/America/New_York".
        var segments = tzid.Trim('/').Split('/');
        for (var take = Math.Min(3, segments.Length); take >= 2; take--)
        {
            var candidate = string.Join('/', segments[^take..]);
            if (GraphTimeZone.TryFind(candidate, out _))
            {
                return candidate;
            }
        }

        if (_timeZones.TryGetValue(tzid, out var vtimezone))
        {
            var location = vtimezone.Properties.FirstOrDefault(p => string.Equals(p.Name, "X-LIC-LOCATION", StringComparison.OrdinalIgnoreCase))?.Value?.ToString()
                           ?? vtimezone.Location;
            if (!string.IsNullOrWhiteSpace(location) && GraphTimeZone.TryFind(location, out _))
            {
                return location;
            }
        }

        return null;
    }

    /// <summary>The UTC offset in force at a wall-clock time per the VTIMEZONE's STANDARD/DAYLIGHT observances.</summary>
    internal static TimeSpan? OffsetFromObservances(VTimeZone vtimezone, DateTime wall)
    {
        DateTime? bestOnset = null;
        TimeSpan? bestOffset = null;
        foreach (var observance in vtimezone.TimeZoneInfos)
        {
            if (observance.OffsetTo is null || observance.DtStart is null)
            {
                continue;
            }

            foreach (var onset in Onsets(observance, wall))
            {
                if (onset <= wall && (bestOnset is null || onset > bestOnset))
                {
                    bestOnset = onset;
                    bestOffset = observance.OffsetTo.Offset;
                }
            }
        }

        return bestOffset;
    }

    private static IEnumerable<DateTime> Onsets(Ical.Net.VTimeZoneInfo observance, DateTime wall)
    {
        var start = DateTime.SpecifyKind(observance.DtStart!.Value, DateTimeKind.Unspecified);
        yield return start;
        if (observance.RecurrenceRule is null || start > wall)
        {
            yield break;
        }

        List<DateTime> occurrences;
        try
        {
            var from = wall.AddYears(-1) > start ? wall.AddYears(-1) : start;
            occurrences = observance
                .GetOccurrences(new CalDateTime(from, false), new EvaluationOptions())
                .Select(o => DateTime.SpecifyKind(o.Period.StartTime.Value, DateTimeKind.Unspecified))
                .TakeWhile(o => o <= wall.AddDays(1))
                .ToList();
        }
        catch (EvaluationException)
        {
            yield break;
        }

        foreach (var occurrence in occurrences)
        {
            yield return occurrence;
        }
    }
}
