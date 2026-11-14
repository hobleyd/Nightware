namespace Nightware.Application.Common.Time;

/// <summary>
/// Resolves Graph time zone names — Windows ids ("AUS Eastern Standard Time"),
/// IANA ids ("Australia/Sydney") or "UTC" — and converts between them.
/// </summary>
public static class GraphTimeZone
{
    public const string Utc = "UTC";

    /// <summary>Finds the zone for a Windows or IANA id. Returns false for unknown names.</summary>
    public static bool TryFind(string? name, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(name) || IsUtc(name))
        {
            return !string.IsNullOrWhiteSpace(name);
        }

        if (TimeZoneInfo.TryFindSystemTimeZoneById(name, out var found))
        {
            zone = found;
            return true;
        }

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(name, out var iana)
            && TimeZoneInfo.TryFindSystemTimeZoneById(iana, out found))
        {
            zone = found;
            return true;
        }

        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(name, out var windows)
            && TimeZoneInfo.TryFindSystemTimeZoneById(windows, out found))
        {
            zone = found;
            return true;
        }

        return false;
    }

    /// <summary>The Windows id Graph uses in <c>mailboxSettings.timeZone</c>, or the input when no mapping exists.</summary>
    public static string ToWindowsId(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (IsUtc(name))
        {
            return Utc;
        }

        return TimeZoneInfo.TryConvertIanaIdToWindowsId(name, out var windows) ? windows : name;
    }

    /// <summary>The IANA id used by iCalendar/CalDAV, or the input when no mapping exists.</summary>
    public static string ToIanaId(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (IsUtc(name))
        {
            return "Etc/UTC";
        }

        return TimeZoneInfo.TryConvertWindowsIdToIanaId(name, out var iana) ? iana : name;
    }

    /// <summary>Expresses an instant as wall-clock time in <paramref name="zone"/> (Graph <c>Prefer: outlook.timezone</c>).</summary>
    public static DateTime ToWallClock(DateTimeOffset instant, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(instant, zone).DateTime, DateTimeKind.Unspecified);
    }

    /// <summary>Interprets a wall-clock time in the named zone as an instant. Unknown zones are treated as UTC.</summary>
    public static DateTimeOffset ToInstant(DateTime wallClock, string? zoneName)
    {
        if (!TryFind(zoneName, out var zone))
        {
            zone = TimeZoneInfo.Utc;
        }

        var unspecified = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
        var offset = zone.GetUtcOffset(unspecified);
        return new DateTimeOffset(unspecified, offset);
    }

    private static bool IsUtc(string name) =>
        name.Equals(Utc, StringComparison.OrdinalIgnoreCase)
        || name.Equals("Etc/UTC", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Coordinated Universal Time", StringComparison.OrdinalIgnoreCase)
        || name.Equals("tzone://Microsoft/Utc", StringComparison.OrdinalIgnoreCase);
}
