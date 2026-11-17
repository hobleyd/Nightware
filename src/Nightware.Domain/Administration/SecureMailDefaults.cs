using System.Net;
using System.Net.Sockets;

namespace Nightware.Domain.Administration;

/// <summary>
/// The strictest SPF and DMARC records the known facts allow, offered to a domain that has none of its own. Only
/// the server's own public addresses may send (<c>-all</c> refuses the rest); without an address the domain's MX
/// hosts stand in. DMARC rejects failing mail, for subdomains too, and asks for strict alignment of both checks.
/// </summary>
public static class SecureMailDefaults
{
    /// <summary>Most addresses named in one SPF record, keeping it well inside one TXT string.</summary>
    public const int MaxAddresses = 6;

    public static SpfRecord Spf(IEnumerable<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        string[] mechanisms =
        [
            .. addresses.Where(IsPublic).Distinct().Take(MaxAddresses)
                .Select(static a => (a.AddressFamily == AddressFamily.InterNetworkV6 ? "ip6:" : "ip4:") + a),
        ];
        return SpfRecord.Parse(mechanisms.Length == 0 ? "v=spf1 mx -all" : $"v=spf1 {string.Join(' ', mechanisms)} -all").Value;
    }

    public static DmarcRecord Dmarc(MailDomain domain)
    {
        ArgumentNullException.ThrowIfNull(domain);
        return DmarcRecord.Parse($"v=DMARC1; p=reject; sp=reject; adkim=s; aspf=s; rua=mailto:postmaster@{domain.Name}").Value;
    }

    /// <summary>
    /// Whether mail from <paramref name="address"/> could reach the internet as it stands: not private (RFC 1918),
    /// shared (CGNAT), loopback, link-local, unique-local, documentation or unspecified.
    /// </summary>
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            return !(b[0] == 10
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && b[1] is >= 64 and <= 127)
                || (b[0] == 169 && b[1] == 254)
                || b[0] == 0
                || b[0] >= 224
                || (b[0] == 192 && b[1] == 0 && b[2] == 2)
                || (b[0] == 198 && b[1] == 51 && b[2] == 100)
                || (b[0] == 203 && b[1] == 0 && b[2] == 113));
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte[] b = address.GetAddressBytes();
            return !(address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast
                || (b[0] & 0xFE) == 0xFC
                || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8));
        }

        return false;
    }
}
