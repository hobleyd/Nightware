using System.Net;

using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Entities;

/// <summary>Where the addresses in a suggested SPF record came from, so the page can say so.</summary>
public enum PublicAddressSource
{
    /// <summary>No public host is configured, or nothing public was found for it: the MX hosts stand in.</summary>
    None,

    /// <summary>The host's A/AAAA records in the entity's own Cloudflare zone.</summary>
    Cloudflare,

    /// <summary>Public DNS for the host.</summary>
    PublicDns,

    /// <summary>Cloudflare proxies the host, so its addresses are Cloudflare's edge, not the server's.</summary>
    Proxied,
}

/// <summary>A suggested SPF and DMARC pair for a domain with none of its own, and where its addresses came from.</summary>
public sealed record MailAuthenticationSuggestion(SpfRecord Spf, DmarcRecord Dmarc, string? Host, IReadOnlyList<IPAddress> Addresses, PublicAddressSource Source);

/// <summary>
/// Builds the strictest SPF and DMARC the known facts allow (<see cref="SecureMailDefaults"/>) for one of an
/// entity's domains. The server's public addresses are those of the configured public host: from the entity's
/// Cloudflare zone when the entity has a token and the host is in a zone it reaches, otherwise from public DNS.
/// A host Cloudflare proxies (seen in the zone, or answering from <see cref="CloudflareEdge"/>) gives none.
/// Writes nothing, and never fails for want of an address: it then suggests the MX hosts. Global administrators
/// only, as they alone set a domain's records.
/// </summary>
public sealed class SuggestMailAuthentication(IAdminActor actor, IEntityRegistry entities, DnsTokenVault vault, IDnsHosting hosting, IPublicDns dns, AdministrationOptions options)
{
    public async Task<Result<MailAuthenticationSuggestion>> ExecuteAsync(string entityId, string domain, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        Result allowed = AdminAuthorization.RequireGlobal(actor.Current);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        if (!MailDomain.TryParse(domain, out MailDomain? mailDomain))
        {
            return Error.Validation("Enter a domain name such as example.com.", "domain");
        }

        Result<Entity> entity = await entities.GetAsync(entityId, cancellationToken).ConfigureAwait(false);
        if (!entity.IsSuccess)
        {
            return entity.Error;
        }

        string? host = options.PublicHost?.Trim().TrimEnd('.');
        (IReadOnlyList<IPAddress> addresses, PublicAddressSource source) = string.IsNullOrEmpty(host)
            ? ([], PublicAddressSource.None)
            : await AddressesAsync(entity.Value, host, cancellationToken).ConfigureAwait(false);
        return new MailAuthenticationSuggestion(SecureMailDefaults.Spf(addresses), SecureMailDefaults.Dmarc(mailDomain), string.IsNullOrEmpty(host) ? null : host, addresses, source);
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
