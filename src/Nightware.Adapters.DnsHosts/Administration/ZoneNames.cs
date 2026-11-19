using System.Security.Cryptography;
using System.Text;

using Nightware.Domain.Administration;

namespace Nightware.Adapters.DnsHosts.Administration;

/// <summary>
/// Names and values as the DNS hosts take them: names relative to the zone (<c>@</c> for its apex), host names in
/// values fully qualified with a trailing dot, TXT as quoted strings. And the ids of records at hosts that keep
/// record sets (one entry per name and type), which have no id of their own.
/// </summary>
internal static class ZoneNames
{
    public const string Apex = "@";

    /// <summary>Recorded beside each value the console creates, where the host keeps a comment.</summary>
    public const string Comment = "Published by the Nightware console";

    private const int TxtStringLength = 255;

    /// <summary>The domain, then each parent up to (not including) the top-level domain: where its zone may be.</summary>
    public static IEnumerable<string> Candidates(MailDomain domain)
    {
        ArgumentNullException.ThrowIfNull(domain);
        string[] labels = domain.Name.Split('.');
        for (int i = 0; i <= labels.Length - 2; i++)
        {
            yield return string.Join('.', labels[i..]);
        }
    }

    /// <summary><paramref name="name"/> relative to <paramref name="zone"/>: <c>@</c> for the apex, <c>mail</c> for mail.acme.example.</summary>
    public static string Relative(string name, string zone)
    {
        string fqdn = Plain(name);
        string apex = Plain(zone);
        return fqdn == apex ? Apex : fqdn.EndsWith("." + apex, StringComparison.Ordinal) ? fqdn[..^(apex.Length + 1)] : fqdn;
    }

    /// <summary>A name as a host lists it (relative, <c>@</c>, or fully qualified) made fully qualified, without the trailing dot.</summary>
    public static string Absolute(string name, string zone)
    {
        string apex = Plain(zone);
        string trimmed = name.Trim();
        if (trimmed is "" or Apex || Plain(trimmed) == apex)
        {
            return apex;
        }

        return trimmed.EndsWith('.') || Plain(trimmed).EndsWith("." + apex, StringComparison.Ordinal) ? Plain(trimmed) : $"{Plain(trimmed)}.{apex}";
    }

    public static string Plain(string name) => name.Trim().TrimEnd('.').ToLowerInvariant();

    public static string Fqdn(string host) => host == "." ? host : Plain(host) + ".";

    /// <summary>
    /// A record's value (<see cref="DnsPublicationPlanner.Canonical"/> form) as zone files write it: the host name in
    /// CNAME, MX and SRV fully qualified, TXT as quoted strings of at most 255 characters.
    /// </summary>
    public static string Presentation(DnsRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        string type = record.Type.Trim().ToUpperInvariant();
        string[] parts = record.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return type switch
        {
            "TXT" => QuotedStrings(record.Value),
            "CNAME" or "NS" or "PTR" => Fqdn(record.Value),
            "MX" when parts.Length == 2 => $"{parts[0]} {Fqdn(parts[1])}",
            "SRV" when parts.Length == 4 => $"{parts[0]} {parts[1]} {parts[2]} {Fqdn(parts[3])}",
            _ => record.Value,
        };
    }

    /// <summary><c>"…" "…"</c>: the value in character-strings of at most 255 characters, quotes and backslashes escaped.</summary>
    public static string QuotedStrings(string value) =>
        value.Length == 0
            ? "\"\""
            : string.Join(' ', value.Chunk(TxtStringLength).Select(static chunk =>
                "\"" + new string(chunk).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\""));

    /// <summary>
    /// The id of one value in a host's record set: its name, type and a hash of the value as the host holds it. The
    /// same while the record is; a changed value is another record, as it would be at a host with record ids.
    /// </summary>
    public static string RecordId(string fqdn, string type, string value)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
        return $"{Plain(fqdn)}/{type.Trim().ToUpperInvariant()}/{hash}";
    }

    /// <summary>A TTL the host accepts: the record's own, or <paramref name="fallback"/>, within its bounds.</summary>
    public static int Ttl(int? ttl, int fallback, int min, int max) => Math.Clamp(ttl ?? fallback, min, max);
}
