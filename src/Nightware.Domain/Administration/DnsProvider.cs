namespace Nightware.Domain.Administration;

/// <summary>
/// A DNS host the console can publish an entity's records to with the entity's own credential. Cloudflare is the
/// first (and the default of a value never set): an entity connected before the others existed is a Cloudflare one.
/// </summary>
public enum DnsProvider
{
    Cloudflare,
    DigitalOcean,
    Gandi,
    Hetzner,
    Route53,
}

public static class DnsProviders
{
    public static IReadOnlyList<DnsProvider> All { get; } = Enum.GetValues<DnsProvider>();

    /// <summary>The provider's name as administrators know it.</summary>
    public static string Name(this DnsProvider provider) => provider switch
    {
        DnsProvider.Cloudflare => "Cloudflare",
        DnsProvider.DigitalOcean => "DigitalOcean",
        DnsProvider.Gandi => "Gandi",
        DnsProvider.Hetzner => "Hetzner",
        DnsProvider.Route53 => "Amazon Route 53",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown DNS provider."),
    };

    /// <summary>
    /// The provider's stable key in stored names (attribute <c>nightware_dns_token:&lt;key&gt;</c>, a sealed token's
    /// purpose) and in forms. Never changes once released: Cloudflare's is what existing entities were stored with.
    /// </summary>
    public static string Key(this DnsProvider provider) => provider switch
    {
        DnsProvider.Cloudflare => "cloudflare",
        DnsProvider.DigitalOcean => "digitalocean",
        DnsProvider.Gandi => "gandi",
        DnsProvider.Hetzner => "hetzner",
        DnsProvider.Route53 => "route53",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown DNS provider."),
    };

    public static bool TryParse(string? key, out DnsProvider provider)
    {
        foreach (DnsProvider candidate in All)
        {
            if (string.Equals(candidate.Key(), key?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                provider = candidate;
                return true;
            }
        }

        provider = default;
        return false;
    }
}
