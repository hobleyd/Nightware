using Ical.Net.DataTypes;
using Nightware.Application.Common.Time;
using Nightware.Domain.Calendar;
using IcalCalendar = Ical.Net.Calendar;

namespace Nightware.Adapters.Dav.Calendar.ICal;

/// <summary>
/// Writes Domain date-times as iCalendar values and collects the zones used so one
/// VTIMEZONE per TZID can be emitted, covering the earliest date written. Windows zone
/// names become IANA TZIDs; UTC and unknown zones are written as UTC.
/// </summary>
internal sealed class ZoneWriter
{
    private readonly Dictionary<string, DateTime> _earliestByZone = new(StringComparer.Ordinal);

    public CalDateTime ToCalDateTime(DateTimeTimeZone value, bool isAllDay)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (isAllDay)
        {
            return new CalDateTime(DateOnly.FromDateTime(value.DateTime));
        }

        var wall = DateTime.SpecifyKind(value.DateTime, DateTimeKind.Unspecified);
        var iana = IanaOf(value.TimeZone);
        if (iana is null)
        {
            return IcalNetCalendarCodec.Utc(GraphTimeZone.ToInstant(wall, value.TimeZone));
        }

        _earliestByZone[iana] = _earliestByZone.TryGetValue(iana, out var earliest) && earliest < wall ? earliest : wall;
        return new CalDateTime(wall, iana, true);
    }

    public void AddTimeZones(IcalCalendar calendar)
    {
        foreach (var (zone, earliest) in _earliestByZone)
        {
            calendar.AddTimeZone(zone, SupportedFrom(earliest), false);
        }
    }

    /// <summary>
    /// The earliest date a generated VTIMEZONE should cover. Ical.Net builds the zone's history from that date to
    /// about a year from now and throws when it lies beyond (an event more than a year ahead); a VTIMEZONE starting
    /// today still carries the recurring observances that apply later, so later dates are clamped to today.
    /// </summary>
    internal static DateTime SupportedFrom(DateTime earliest)
    {
        var dayBefore = earliest.AddDays(-1);
        var today = DateTime.UtcNow.Date;
        return dayBefore < today ? dayBefore : today;
    }

    /// <summary>The IANA id Ical.Net/NodaTime can generate a VTIMEZONE for, or null for UTC/unknown zones.</summary>
    internal static string? IanaOf(string? zoneName)
    {
        if (string.IsNullOrWhiteSpace(zoneName) || !GraphTimeZone.TryFind(zoneName, out _))
        {
            return null;
        }

        var iana = GraphTimeZone.ToIanaId(zoneName);
        if (iana is "Etc/UTC" or "UTC" or "Etc/GMT")
        {
            return null;
        }

        return NodaTime.DateTimeZoneProviders.Tzdb.GetZoneOrNull(iana) is null ? null : iana;
    }
}
