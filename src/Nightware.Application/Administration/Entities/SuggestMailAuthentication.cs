using System.Net;

using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Entities;

/// <summary>Where the addresses in a suggested SPF record came from, so the page can say so.</summary>
public enum PublicAddressSource
{
    /// <summary>No public host is configured, or nothing public was found for it: the MX hosts stand in.</summary>
    None,

    /// <summary>The host's A/AAAA records in the entity's own zone at its DNS host.</summary>
    Zone,

    /// <summary>Public DNS for the host.</summary>
    PublicDns,

    /// <summary>Cloudflare proxies the host, so its addresses are Cloudflare's edge, not the server's.</summary>
    Proxied,
}

/// <summary>A suggested SPF and DMARC pair for a domain with none of its own, and where its addresses came from.</summary>
public sealed record MailAuthenticationSuggestion(SpfRecord Spf, DmarcRecord Dmarc, string? Host, IReadOnlyList<IPAddress> Addresses, PublicAddressSource Source);

/// <summary>
/// Builds the strictest SPF and DMARC the known facts allow (<see cref="SecureMailDefaults"/>) for one of an
/// entity's domains, with the server's public addresses from <see cref="PublicAddresses"/>. Writes nothing, and
/// never fails for want of an address: it then suggests the MX hosts. Global administrators only, as they alone
/// set a domain's records.
/// </summary>
public sealed class SuggestMailAuthentication(IAdminActor actor, IEntityRegistry entities, PublicAddresses publicAddresses)
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

        PublicAddressLookup found = await publicAddresses.FindAsync(entity.Value, cancellationToken).ConfigureAwait(false);
        return new MailAuthenticationSuggestion(SecureMailDefaults.Spf(found.Addresses), SecureMailDefaults.Dmarc(mailDomain), found.Host, found.Addresses, found.Source);
    }
}
