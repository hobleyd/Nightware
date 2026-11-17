using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Entities;

/// <summary>What publishing a domain's records to Cloudflare would change, read fresh from the zone.</summary>
public sealed record DnsPublicationPreview(MailDomain Domain, DnsPublicationPlan Plan);

/// <summary>
/// Compares the records one of an entity's domains needs (<see cref="GetEntityDnsRecords"/>) with its Cloudflare
/// zone and plans the changes (<see cref="DnsPublicationPlanner"/>). Writes nothing. Global administrators only:
/// it uses the entity's token, and publishing is theirs.
/// </summary>
public sealed class PlanDnsPublication(IAdminActor actor, GetEntityDnsRecords records, IDnsHosting hosting, DnsTokenVault vault)
{
    public async Task<Result<DnsPublicationPreview>> ExecuteAsync(string entityId, string domain, CancellationToken cancellationToken)
    {
        Result<(DnsPublicationPreview Preview, DnsProviderToken Token)> prepared = await PrepareAsync(entityId, domain, cancellationToken).ConfigureAwait(false);
        return prepared.Map(static p => p.Preview);
    }

    /// <summary>The plan together with the token it was read with, for <see cref="PublishDnsRecords"/>.</summary>
    internal async Task<Result<(DnsPublicationPreview Preview, DnsProviderToken Token)>> PrepareAsync(string entityId, string domain, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        Result allowed = AdminAuthorization.RequireGlobal(actor.Current);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        Result<DomainDns> dns = await records.ExecuteAsync(entityId, domain, cancellationToken).ConfigureAwait(false);
        if (!dns.IsSuccess)
        {
            return dns.Error;
        }

        if (dns.Value.Records.Count == 0)
        {
            return Error.Validation($"The mail server has no records for {dns.Value.Domain} yet, so there is nothing to publish.", "domain");
        }

        Result<DnsProviderToken> token = await vault.OpenAsync(entityId, cancellationToken).ConfigureAwait(false);
        if (!token.IsSuccess)
        {
            return token.Error;
        }

        Result<DnsZone> zone = await hosting.FindZoneAsync(token.Value, dns.Value.Domain, cancellationToken).ConfigureAwait(false);
        if (!zone.IsSuccess)
        {
            return zone.Error.Kind == ErrorKind.NotFound
                ? Error.Validation($"The entity's Cloudflare token reaches no zone for {dns.Value.Domain}. Add the zone to the token's permissions, or publish the records by hand.", "dnsToken")
                : zone.Error;
        }

        Result<IReadOnlyList<ZoneRecord>> existing = await hosting.ListRecordsAsync(token.Value, zone.Value, cancellationToken).ConfigureAwait(false);
        if (!existing.IsSuccess)
        {
            return existing.Error;
        }

        DnsPublicationPlan plan = DnsPublicationPlanner.Plan(zone.Value, dns.Value.Records, existing.Value);
        return (new DnsPublicationPreview(dns.Value.Domain, plan), token.Value);
    }
}
