using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Entities;

/// <summary>The records a domain should publish, and the SPF/DMARC choice they carry.</summary>
public sealed record DomainDns(Entity Entity, MailDomain Domain, IReadOnlyList<DnsRecord> Records, DomainMailPolicy Policy);

/// <summary>
/// The DNS records one of an entity's domains needs (MX, SPF, DKIM, DMARC, …): the mail server's, with the
/// entity's own SPF and DMARC records in place of the server's (<see cref="DomainMailPolicy.ApplyTo"/>). Where neither
/// the server nor the entity has one, the hardened records go in, SPF naming the server's public addresses. An MX
/// host inside the domain that the server's zone gives no address gets the server's public addresses
/// (<see cref="MailHostAddresses"/>), or its mail would bounce.
/// </summary>
public sealed class GetEntityDnsRecords(IAdminActor actor, IEntityRegistry entities, IMailDomains domains, PublicAddresses publicAddresses)
{
    public async Task<Result<DomainDns>> ExecuteAsync(string entityId, string domain, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        Result allowed = AdminAuthorization.RequireEntity(actor.Current, entityId);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        Result<Entity> entity = await entities.GetAsync(entityId, cancellationToken).ConfigureAwait(false);
        if (!entity.IsSuccess)
        {
            return entity.Error;
        }

        if (!MailDomain.TryParse(domain, out MailDomain? mailDomain) || !entity.Value.Domains.Contains(mailDomain))
        {
            return Error.NotFound("The entity has no such domain.", "domain");
        }

        DomainMailPolicy policy = entity.Value.MailPolicyFor(mailDomain);
        Result<IReadOnlyList<DnsRecord>> records = await domains.GetDnsRecordsAsync(mailDomain, cancellationToken).ConfigureAwait(false);
        if (!records.IsSuccess)
        {
            return records.Error;
        }

        // Only when the hardened SPF fallback or an MX host's address goes in is the server's address worth looking up.
        IReadOnlyList<System.Net.IPAddress> addresses = policy.NeedsSpfFallback(mailDomain, records.Value) || MailHostAddresses.Missing(mailDomain, records.Value).Count > 0
            ? (await publicAddresses.FindAsync(entity.Value, cancellationToken).ConfigureAwait(false)).Addresses
            : [];
        IReadOnlyList<DnsRecord> needed = MailHostAddresses.AddTo(mailDomain, policy.ApplyTo(mailDomain, records.Value, addresses), addresses);
        return new DomainDns(entity.Value, mailDomain, needed, policy);
    }
}
