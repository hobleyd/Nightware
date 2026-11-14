using System.Globalization;
using System.Text;
using Nightware.Domain.Calendar;

namespace Nightware.Adapters.Dav.Calendar.ICal;

/// <summary>
/// RFC 6638 free-busy over the scheduling outbox: builds the VFREEBUSY REQUEST and reads the
/// FREEBUSY periods of each recipient's REPLY. FBTYPE BUSY → busy, BUSY-TENTATIVE → tentative,
/// BUSY-UNAVAILABLE → oof, FREE → free. Periods may be <c>start/end</c> or <c>start/duration</c>.
/// </summary>
public sealed class FreeBusyCodec
{
    private const string UtcFormat = "yyyyMMdd'T'HHmmss'Z'";
    private readonly TimeProvider _timeProvider;

    public FreeBusyCodec(TimeProvider timeProvider) =>
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <summary>The body to POST to the organizer's <c>schedule-outbox-URL</c>.</summary>
    public string BuildRequest(string organizer, IEnumerable<string> attendees, DateTimeOffset start, DateTimeOffset end, string? uid = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizer);
        ArgumentNullException.ThrowIfNull(attendees);
        var builder = new StringBuilder()
            .Append("BEGIN:VCALENDAR\r\n")
            .Append("VERSION:2.0\r\n")
            .Append("PRODID:").Append(IcalNetCalendarCodec.ProductId).Append("\r\n")
            .Append("METHOD:REQUEST\r\n")
            .Append("BEGIN:VFREEBUSY\r\n")
            .Append("UID:").Append(uid ?? Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)).Append("\r\n")
            .Append("DTSTAMP:").Append(Format(_timeProvider.GetUtcNow())).Append("\r\n")
            .Append("DTSTART:").Append(Format(start)).Append("\r\n")
            .Append("DTEND:").Append(Format(end)).Append("\r\n")
            .Append("ORGANIZER:").Append(IcalNetCalendarCodec.MailTo(organizer)).Append("\r\n");
        foreach (var attendee in attendees.Where(a => !string.IsNullOrWhiteSpace(a)))
        {
            builder.Append("ATTENDEE:").Append(IcalNetCalendarCodec.MailTo(attendee)).Append("\r\n");
        }

        return builder.Append("END:VFREEBUSY\r\nEND:VCALENDAR\r\n").ToString();
    }

    /// <summary>The busy periods in a VFREEBUSY (a scheduling REPLY or a free-busy-query answer), in order.</summary>
    public static IReadOnlyList<ScheduleItem> ParseReply(string? ics)
    {
        if (string.IsNullOrWhiteSpace(ics))
        {
            return [];
        }

        var items = new List<ScheduleItem>();
        var inFreeBusy = false;
        foreach (var line in Unfold(ics))
        {
            if (line.Equals("BEGIN:VFREEBUSY", StringComparison.OrdinalIgnoreCase))
            {
                inFreeBusy = true;
            }
            else if (line.Equals("END:VFREEBUSY", StringComparison.OrdinalIgnoreCase))
            {
                inFreeBusy = false;
            }
            else if (inFreeBusy && line.StartsWith("FREEBUSY", StringComparison.OrdinalIgnoreCase) && line.Length > 8 && line[8] is ';' or ':')
            {
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                var status = StatusOf(line[..colon]);
                foreach (var period in line[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (ParsePeriod(period) is { } parsed)
                    {
                        items.Add(new ScheduleItem(parsed.Start, parsed.End, status));
                    }
                }
            }
        }

        return items.OrderBy(i => i.Start).ToList();
    }

    private static FreeBusyStatus StatusOf(string head)
    {
        var marker = "FBTYPE=";
        var index = head.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        var type = index < 0 ? "BUSY" : head[(index + marker.Length)..].Split(';')[0].Trim('"').ToUpperInvariant();
        return type switch
        {
            "FREE" => FreeBusyStatus.Free,
            "BUSY-TENTATIVE" => FreeBusyStatus.Tentative,
            "BUSY-UNAVAILABLE" => FreeBusyStatus.Oof,
            _ => FreeBusyStatus.Busy,
        };
    }

    private static (DateTimeOffset Start, DateTimeOffset End)? ParsePeriod(string period)
    {
        var parts = period.Split('/');
        if (parts.Length != 2 || !TryParseUtc(parts[0], out var start))
        {
            return null;
        }

        if (TryParseUtc(parts[1], out var end))
        {
            return (start, end);
        }

        return Ical.Net.DataTypes.Duration.Parse(parts[1]) is { } duration
            ? (start, start + duration.ToTimeSpanUnspecified())
            : null;
    }

    private static bool TryParseUtc(string value, out DateTimeOffset instant) =>
        DateTimeOffset.TryParseExact(value, UtcFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out instant);

    private static string Format(DateTimeOffset value) => value.UtcDateTime.ToString(UtcFormat, CultureInfo.InvariantCulture);

    private static IEnumerable<string> Unfold(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var current = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                current.Append(line, 1, line.Length - 1);
                continue;
            }

            if (current.Length > 0)
            {
                yield return current.ToString();
            }

            current.Clear().Append(line);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }
}
