using System.Net;

using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Entities;

/// <summary>The server's public addresses as found for one entity, where they came from, and for which host.</summary>
public sealed record PublicAddressLookup(string? Host, IReadOnlyList<IPAddress> Addresses, PublicAddressSource Source);

/// <summary>
/// Finds the server's public addresses: those of the configured public host (<see cref="AdministrationOptions.PublicHost"/>),
/// from the entity's Cloudflare zone when the entity has a token and the host is in a zone it reaches, otherwise from
/// public DNS. A host Cloudflare proxies (seen in the zone, or answering from <see cref="CloudflareEdge"/>) gives
/// none, as do private addresses and failed lookups. Never fails: no address is an answer too.
/// </summary>
public sealed class PublicAddresses(DnsTokenVault vault, IDnsHosting hosting, IPublicDns dns, AdministrationOptions options)
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
        return new PublicAddressLookup(host, addresses, source);
    }

    private async Task<(IReadOnlyList<IPAddress>, PublicAddressSource)> AddressesAsync(Entity entity, string host, CancellationToken cancellationToken)
    {
        if (entity.HasDnsToken && MailDomain.TryParse(host, out MailDomain? hostName))
        {
            (IReadOnlyList<IPAddress> fromZone, bool proxied) = await FromCloudflareAsync(entity.Id, hostName, cancellationToken).ConfigureAwait(false);
            if (proxied)
            {
                // Public DNS would only return the same edge addresses.
                return ([], PublicAddressSource.Proxied);
            }

            if (fromZone.Count > 0)
            {
                return (fromZone, PublicAddressSource.Cloudflare);
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
    private async Task<(IReadOnlyList<IPAddress>, bool Proxied)> FromCloudflareAsync(string entityId, MailDomain host, CancellationToken cancellationToken)
    {
        Result<DnsProviderToken> token = await vault.OpenAsync(entityId, cancellationToken).ConfigureAwait(false);
        if (!token.IsSuccess)
        {
            return ([], false);
        }

        Result<DnsZone> zone = await hosting.FindZoneAsync(token.Value, host, cancellationToken).ConfigureAwait(false);
        if (!zone.IsSuccess)
        {
            return ([], false);
        }

        Result<IReadOnlyList<ZoneRecord>> records = await hosting.ListRecordsAsync(token.Value, zone.Value, cancellationToken).ConfigureAwait(false);
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
