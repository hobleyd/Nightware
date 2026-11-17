using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Entities;

/// <summary>The DNS records one of an entity's domains needs (MX, SPF, DKIM, DMARC, …), from the mail server.</summary>
public sealed class GetEntityDnsRecords(IAdminActor actor, IEntityRegistry entities, IMailDomains domains)
{
    public async Task<Result<IReadOnlyList<DnsRecord>>> ExecuteAsync(string entityId, string domain, CancellationToken cancellationToken)
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

        return await domains.GetDnsRecordsAsync(mailDomain, cancellationToken).ConfigureAwait(false);
    }
}
