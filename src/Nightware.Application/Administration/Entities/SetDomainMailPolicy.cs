using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Entities;

/// <summary>
/// Chooses the SPF and DMARC records one of an entity's domains publishes, in place of the mail server's own.
/// A blank record returns it to the mail server's default. Global administrators only, like every domain change.
/// </summary>
public sealed class SetDomainMailPolicy(IAdminActor actor, IEntityRegistry entities, IAuditLog audit)
{
    public async Task<Result<DomainMailPolicy>> ExecuteAsync(string entityId, string domain, string? spf, string? dmarc, CancellationToken cancellationToken)
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

        // Blank means the mail server's default (null); anything else must parse.
        Result<SpfRecord?> spfRecord = string.IsNullOrWhiteSpace(spf) ? Result<SpfRecord?>.Success(null) : SpfRecord.Parse(spf).Map(static r => (SpfRecord?)r);
        if (!spfRecord.IsSuccess)
        {
            return spfRecord.Error;
        }

        Result<DmarcRecord?> dmarcRecord = string.IsNullOrWhiteSpace(dmarc) ? Result<DmarcRecord?>.Success(null) : DmarcRecord.Parse(dmarc).Map(static r => (DmarcRecord?)r);
        if (!dmarcRecord.IsSuccess)
        {
            return dmarcRecord.Error;
        }

        DomainMailPolicy policy = new(spfRecord.Value, dmarcRecord.Value);
        Result<Entity> updated = await entities.SetMailPolicyAsync(entityId, mailDomain, policy, cancellationToken).ConfigureAwait(false);
        audit.Record(new AuditEvent("entity.domain.mail-policy", actor.Current.UserId, updated.IsSuccess)
        {
            EntityId = entityId,
            Details = new Dictionary<string, string?> { ["domain"] = mailDomain.Name, ["spf"] = policy.Spf?.Value, ["dmarc"] = policy.Dmarc?.Value },
        });
        return updated.IsSuccess ? policy : updated.Error;
    }
}
