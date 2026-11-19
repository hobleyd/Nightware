using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Entities;

/// <summary>What publishing a domain's records to the entity's DNS host would change, read fresh from the zone.</summary>
public sealed record DnsPublicationPreview(MailDomain Domain, DnsPublicationPlan Plan)
{
    /// <summary>The DNS host the zone is at.</summary>
    public DnsProvider Provider { get; init; }
}

/// <summary>
/// Compares the records one of an entity's domains needs (<see cref="GetEntityDnsRecords"/>) with its zone at the
/// entity's DNS host and plans the changes (<see cref="DnsPublicationPlanner"/>). Writes nothing. Global administrators only:
/// it uses the entity's token, and publishing is theirs.
/// </summary>
public sealed class PlanDnsPublication(IAdminActor actor, GetEntityDnsRecords records, DnsHosts hosts)
{
    public async Task<Result<DnsPublicationPreview>> ExecuteAsync(string entityId, string domain, CancellationToken cancellationToken)
    {
        Result<(DnsPublicationPreview Preview, DnsHostSession Host)> prepared = await PrepareAsync(entityId, domain, cancellationToken).ConfigureAwait(false);
        return prepared.Map(static p => p.Preview);
    }

    /// <summary>The plan together with the host it was read from, for <see cref="PublishDnsRecords"/>.</summary>
    internal async Task<Result<(DnsPublicationPreview Preview, DnsHostSession Host)>> PrepareAsync(string entityId, string domain, CancellationToken cancellationToken)
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

        Result<DnsHostSession> host = await hosts.ConnectAsync(entityId, cancellationToken).ConfigureAwait(false);
        if (!host.IsSuccess)
        {
            return host.Error;
        }

        Result<DnsZone> zone = await host.Value.FindZoneAsync(dns.Value.Domain, cancellationToken).ConfigureAwait(false);
        if (!zone.IsSuccess)
        {
            return zone.Error.Kind == ErrorKind.NotFound
                ? Error.Validation($"The entity's {host.Value.Provider.Name()} credential reaches no zone for {dns.Value.Domain}. Let it edit the domain's zone there, or publish the records by hand.", "dnsToken")
                : zone.Error;
        }

        Result<IReadOnlyList<ZoneRecord>> existing = await host.Value.ListRecordsAsync(zone.Value, cancellationToken).ConfigureAwait(false);
        if (!existing.IsSuccess)
        {
            return existing.Error;
        }

        DnsPublicationPlan plan = DnsPublicationPlanner.Plan(zone.Value, dns.Value.Records, existing.Value);
        return (new DnsPublicationPreview(dns.Value.Domain, plan) { Provider = host.Value.Provider }, host.Value);
    }
}
