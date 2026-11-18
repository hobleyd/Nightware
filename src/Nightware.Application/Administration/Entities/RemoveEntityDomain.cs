using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Entities;

/// <summary>
/// Removes a domain from an entity: destroys it on the mail server, then removes it in Keycloak. Refused while any
/// member has an address on it, and while any mail account does (the mail server refuses). The mail server goes
/// first so that a failure leaves the domain on the entity, where removing it can be tried again; the other way
/// round, the mail server would go on treating a domain no entity shows as its own, refusing mail sent to it.
/// </summary>
public sealed class RemoveEntityDomain(IAdminActor actor, IEntityRegistry entities, IMailDomains mailDomains, IAuditLog audit)
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

        // Already gone from the mail server (removed by hand, or an earlier attempt failed in Keycloak): carry on.
        Result destroyed = await mailDomains.DestroyAsync(mailDomain, cancellationToken).ConfigureAwait(false);
        bool absent = !destroyed.IsSuccess && destroyed.Error.Kind == ErrorKind.NotFound;
        if (absent)
        {
            // An earlier attempt may have destroyed it without the reload that makes SMTP forget it. Best effort:
            // the domain is gone either way, and a restart of the mail server reloads too.
            await mailDomains.ReloadSettingsAsync(cancellationToken).ConfigureAwait(false);
        }

        Result<Entity> updated = destroyed.IsSuccess || absent
            ? await entities.RemoveDomainAsync(entityId, mailDomain, cancellationToken).ConfigureAwait(false)
            : destroyed.Error!;
        audit.Record(new AuditEvent("entity.domain.removed", actor.Current.UserId, updated.IsSuccess)
        {
            EntityId = entityId,
            Details = new Dictionary<string, string?>
            {
                ["domain"] = mailDomain.Name,
                ["mailServer"] = destroyed.IsSuccess ? "destroyed" : absent ? "absent" : "failed",
            },
        });
        return updated;
    }

    private static bool OnDomain(string address, MailDomain domain) =>
        MailAddress.TryParse(address, out MailAddress? parsed) ? parsed.Domain == domain : address.EndsWith("@" + domain.Name, StringComparison.OrdinalIgnoreCase);
}
