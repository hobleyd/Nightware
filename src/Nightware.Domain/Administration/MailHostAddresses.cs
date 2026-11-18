using System.Net;
using System.Net.Sockets;

namespace Nightware.Domain.Administration;

/// <summary>
/// The address records a domain's own MX hosts need. The mail server's zone names its MX host (often
/// <c>mail.&lt;domain&gt;</c>) but not the host's address, and an MX whose host does not resolve receives nothing:
/// senders bounce the mail. So an MX host inside the domain without an A or AAAA record gets one for each of the
/// server's public addresses (A for IPv4, AAAA for IPv6). Hosts outside the domain belong to another zone and are
/// left alone; without a known address nothing is added (an MX host must not be a CNAME, RFC 2181 §10.3).
/// </summary>
public static class MailHostAddresses
{
    /// <summary>The MX hosts inside <paramref name="domain"/> that <paramref name="records"/> give no address.</summary>
    public static IReadOnlyList<string> Missing(MailDomain domain, IReadOnlyList<DnsRecord> records)
    {
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(records);
        return
        [
            .. records.Where(static r => r.Type.Trim().Equals("MX", StringComparison.OrdinalIgnoreCase))
                .Select(static r => Host(r.Value))
                .OfType<string>()
                .Where(h => h.Equals(domain.Name, StringComparison.OrdinalIgnoreCase) || h.EndsWith("." + domain.Name, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(h => !records.Any(r => r.Type.Trim() is "A" or "AAAA" && r.Name.TrimEnd('.').Equals(h, StringComparison.OrdinalIgnoreCase))),
        ];
    }

    /// <summary>
    /// <paramref name="records"/> with an A/AAAA record for each public address of <paramref name="addresses"/> after
    /// the MX record naming each <see cref="Missing"/> host (with the MX record's TTL).
    /// </summary>
    public static IReadOnlyList<DnsRecord> AddTo(MailDomain domain, IReadOnlyList<DnsRecord> records, IEnumerable<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        IPAddress[] usable = [.. addresses.Where(SecureMailDefaults.IsPublic).Distinct().Take(SecureMailDefaults.MaxAddresses)];
        IReadOnlyList<string> missing = Missing(domain, records);
        if (usable.Length == 0 || missing.Count == 0)
        {
            return records;
        }

        List<DnsRecord> result = [.. records];
        foreach (string host in missing)
        {
            int mx = result.FindIndex(r => r.Type.Trim().Equals("MX", StringComparison.OrdinalIgnoreCase) && string.Equals(Host(r.Value), host, StringComparison.OrdinalIgnoreCase));
            result.InsertRange(mx + 1, usable.Select(a => new DnsRecord(host, a.AddressFamily == AddressFamily.InterNetworkV6 ? "AAAA" : "A", result[mx].Ttl, a.ToString())));
        }

        return result;
    }

    /// <summary>The host of an MX value (<c>"10 mail.acme.example."</c>), lower-case without the trailing dot.</summary>
    private static string? Host(string value)
    {
        string[] parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? host = parts.Length > 0 ? parts[^1].TrimEnd('.') : null;
        return string.IsNullOrEmpty(host) ? null : host.ToLowerInvariant();
    }
}
