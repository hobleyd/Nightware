using Nightware.Application.Common.Time;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Calendar;

/// <summary>Input checks shared by the calendar use cases. Each returns null when the input is acceptable.</summary>
internal static class CalendarRules
{
    /// <summary>A wall-clock time must name a zone Graph knows (Windows or IANA id, or UTC).</summary>
    public static Error? KnownZone(DateTimeTimeZone? value, string target) =>
        value is null || GraphTimeZone.TryFind(value.TimeZone, out _)
            ? null
            : Error.Validation($"'{value.TimeZone}' is not a supported time zone.", target);

    /// <summary>Both ends in known zones, and the end not before the start.</summary>
    public static Error? Ordered(DateTimeTimeZone start, DateTimeTimeZone end, string target, bool allowEmpty = true)
    {
        if ((KnownZone(start, target) ?? KnownZone(end, target)) is { } zone)
        {
            return zone;
        }

        var span = ToInstant(end) - ToInstant(start);
        return span < TimeSpan.Zero || (!allowEmpty && span == TimeSpan.Zero)
            ? Error.Validation("The end time must be after the start time.", target)
            : null;
    }

    public static Error? Addresses(IEnumerable<EmailAddress> addresses, string target) =>
        addresses.FirstOrDefault(a => !IsAddress(a.Address)) is { } bad
            ? Error.Validation($"'{bad.Address}' is not a valid email address.", target)
            : null;

    public static bool IsAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var at = address.IndexOf('@', StringComparison.Ordinal);
        return at > 0 && at < address.Length - 1 && at == address.LastIndexOf('@') && !address.Any(char.IsWhiteSpace);
    }

    public static DateTimeOffset ToInstant(DateTimeTimeZone value) => GraphTimeZone.ToInstant(value.DateTime, value.TimeZone);
}
