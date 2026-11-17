using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Entities;

/// <summary>
/// Removes a domain from an entity in Keycloak. Refused while any member has an address on it. The mail
/// server keeps the domain (the console holds no permission to destroy domains).
/// </summary>
public sealed class RemoveEntityDomain(IAdminActor actor, IEntityRegistry entities, IAuditLog audit)
{
    public async Task<Result<Entity>> ExecuteAsync(string entityId, string domain, CancellationToken cancellationToken)
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

        Result<IReadOnlyList<IdentityAccount>> members = await entities.ListMembersAsync(entityId, cancellationToken).ConfigureAwait(false);
        if (!members.IsSuccess)
        {
            return members.Error;
        }

        int inUse = members.Value.Count(m => m.Aliases.Prepend(m.Email).Any(a => OnDomain(a, mailDomain)));
        if (inUse > 0)
        {
            return Error.Conflict($"{inUse} user(s) of this entity have addresses on {mailDomain}; change them first.", "domain");
        }

        Result<Entity> updated = await entities.RemoveDomainAsync(entityId, mailDomain, cancellationToken).ConfigureAwait(false);
        audit.Record(new AuditEvent("entity.domain.removed", actor.Current.UserId, updated.IsSuccess)
        {
            EntityId = entityId,
            Details = new Dictionary<string, string?> { ["domain"] = mailDomain.Name },
        });
        return updated;
    }

    private static bool OnDomain(string address, MailDomain domain) =>
        MailAddress.TryParse(address, out MailAddress? parsed) ? parsed.Domain == domain : address.EndsWith("@" + domain.Name, StringComparison.OrdinalIgnoreCase);
}
