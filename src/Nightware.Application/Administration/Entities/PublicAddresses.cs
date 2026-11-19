using System.Net;

using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Entities;

/// <summary>The server's public addresses as found for one entity, where they came from, and for which host.</summary>
public sealed record PublicAddressLookup(string? Host, IReadOnlyList<IPAddress> Addresses, PublicAddressSource Source);

/// <summary>
/// Finds the server's public addresses: those of the configured public host (<see cref="AdministrationOptions.PublicHost"/>),
/// from the entity's zone at its DNS host when the entity has a credential and the host is in a zone it reaches, otherwise from
/// public DNS. A host Cloudflare proxies (seen in the zone, or answering from <see cref="CloudflareEdge"/>) gives
/// none, as do private addresses and failed lookups. Never fails: no address is an answer too. The addresses are
/// sorted (IPv4 first), so records built from them do not change with the order DNS happened to answer in.
/// </summary>
public sealed class PublicAddresses(DnsHosts hosts, IPublicDns dns, AdministrationOptions options)
{
    public async Task<PublicAddressLookup> FindAsync(Entity entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);
        string? host = options.PublicHost?.Trim().TrimEnd('.');
        if (string.IsNullOrEmpty(host))
        {
            return new PublicAddressLookup(null, [], PublicAddressSource.None);
        }

        (IReadOnlyList<IPAddress> addresses, PublicAddressSource source) = await AddressesAsync(entity, host, cancellationToken).ConfigureAwait(false);
        return new PublicAddressLookup(host, [.. addresses.Order(AddressOrder.Instance)], source);
    }

    /// <summary>IPv4 before IPv6, then by the address's bytes.</summary>
    private sealed class AddressOrder : IComparer<IPAddress>
    {
        public static AddressOrder Instance { get; } = new();

        public int Compare(IPAddress? x, IPAddress? y) =>
            x is null || y is null ? Comparer<object>.Default.Compare(x, y)
            : x.AddressFamily != y.AddressFamily ? x.AddressFamily.CompareTo(y.AddressFamily)
            : x.GetAddressBytes().AsSpan().SequenceCompareTo(y.GetAddressBytes());
    }

    private async Task<(IReadOnlyList<IPAddress>, PublicAddressSource)> AddressesAsync(Entity entity, string host, CancellationToken cancellationToken)
    {
        if (entity.HasDnsToken && MailDomain.TryParse(host, out MailDomain? hostName))
        {
            (IReadOnlyList<IPAddress> fromZone, bool proxied) = await FromZoneAsync(entity.Id, hostName, cancellationToken).ConfigureAwait(false);
            if (proxied)
            {
                // Public DNS would only return the same edge addresses.
                return ([], PublicAddressSource.Proxied);
            }

            if (fromZone.Count > 0)
            {
                return (fromZone, PublicAddressSource.Zone);
            }
        }

        Result<IReadOnlyList<IPAddress>> resolved = await dns.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<IPAddress> found = resolved.IsSuccess ? [.. resolved.Value.Where(SecureMailDefaults.IsPublic)] : [];
        if (found.Any(CloudflareEdge.Contains))
        {
            // Proxied from a zone the entity's token cannot see: these are Cloudflare's addresses, not the server's.
            return ([], PublicAddressSource.Proxied);
        }

        return (found, found.Count > 0 ? PublicAddressSource.PublicDns : PublicAddressSource.None);
    }

    /// <summary>The host's unproxied public A/AAAA addresses in the entity's zone; nothing when the token cannot read it.</summary>
    private async Task<(IReadOnlyList<IPAddress>, bool Proxied)> FromZoneAsync(string entityId, MailDomain host, CancellationToken cancellationToken)
    {
        Result<DnsHostSession> dnsHost = await hosts.ConnectAsync(entityId, cancellationToken).ConfigureAwait(false);
        if (!dnsHost.IsSuccess)
        {
            return ([], false);
        }

        Result<DnsZone> zone = await dnsHost.Value.FindZoneAsync(host, cancellationToken).ConfigureAwait(false);
        if (!zone.IsSuccess)
        {
            return ([], false);
        }

        Result<IReadOnlyList<ZoneRecord>> records = await dnsHost.Value.ListRecordsAsync(zone.Value, cancellationToken).ConfigureAwait(false);
        if (!records.IsSuccess)
        {
            return ([], false);
        }

        ZoneRecord[] forHost =
        [
            .. records.Value.Where(r => r.Record.Type is "A" or "AAAA"
                && r.Record.Name.TrimEnd('.').Equals(host.Name, StringComparison.OrdinalIgnoreCase)),
        ];
        if (forHost.Any(static r => r.Proxied))
        {
            return ([], true);
        }

        return ([.. forHost.Select(static r => IPAddress.TryParse(r.Record.Value, out IPAddress? a) ? a : null).OfType<IPAddress>().Where(SecureMailDefaults.IsPublic)], false);
    }
}
