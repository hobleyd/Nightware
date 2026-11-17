using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Entities;

/// <summary>
/// Adds a domain to an entity, or repairs one it already has: Keycloak first, then the mail server.
/// Idempotent, so "Repair" on a domain the mail server is missing is the same call.
/// </summary>
public sealed class AddEntityDomain(IAdminActor actor, IEntityRegistry entities, DomainProvisioning domains, IAuditLog audit)
{
    public async Task<Result<DomainStatus>> ExecuteAsync(string entityId, string domain, CancellationToken cancellationToken)
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

        Result<Entity> entity = await entities.AddDomainAsync(entityId, mailDomain, cancellationToken).ConfigureAwait(false);
        if (!entity.IsSuccess)
        {
            Record(entityId, mailDomain, false, null);
            return entity.Error;
        }

        DomainStatus status = await domains.EnsureAsync(entity.Value, mailDomain, cancellationToken).ConfigureAwait(false);
        Record(entityId, mailDomain, true, status.Mail.State);
        return status;
    }

    private void Record(string entityId, MailDomain domain, bool succeeded, LinkState? mail) =>
        audit.Record(new AuditEvent("entity.domain.added", actor.Current.UserId, succeeded)
        {
            EntityId = entityId,
            Details = new Dictionary<string, string?> { ["domain"] = domain.Name, ["mail"] = mail?.ToString() },
        });
}
