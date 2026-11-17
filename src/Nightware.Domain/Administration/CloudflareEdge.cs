using System.Net;

namespace Nightware.Domain.Administration;

/// <summary>
/// Cloudflare's proxy (edge) address ranges, as published at https://www.cloudflare.com/ips-v4 and
/// https://www.cloudflare.com/ips-v6 (taken November 2026). A host whose public DNS answers fall in them is
/// proxied, so those addresses are Cloudflare's, never the server's own, and must not be put in an SPF record.
/// </summary>
public static class CloudflareEdge
{
    private static readonly IPNetwork[] Ranges =
    [
        .. new[]
        {
            "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22", "141.101.64.0/18",
            "108.162.192.0/18", "190.93.240.0/20", "188.114.96.0/20", "197.234.240.0/22", "198.41.128.0/17",
            "162.158.0.0/15", "104.16.0.0/13", "104.24.0.0/14", "172.64.0.0/13", "131.0.72.0/22",
            "2400:cb00::/32", "2606:4700::/32", "2803:f800::/32", "2405:b500::/32", "2405:8100::/32",
            "2a06:98c0::/29", "2c0f:f248::/32",
        }.Select(IPNetwork.Parse),
    ];

    public static bool Contains(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        IPAddress plain = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return Ranges.Any(r => r.Contains(plain));
    }
}
