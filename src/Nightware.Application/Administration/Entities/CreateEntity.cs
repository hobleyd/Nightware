using System.Text.RegularExpressions;

using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Entities;

/// <summary>
/// Creates an entity with its first domain: the Keycloak organization first (it enforces that a domain
/// belongs to one entity), then the domain on the mail server. A mail-server failure leaves the domain
/// <see cref="LinkState.Missing"/> for a later repair; the organization is never rolled back.
/// </summary>
public sealed partial class CreateEntity(IAdminActor actor, IEntityRegistry entities, DomainProvisioning domains, IAuditLog audit)
{
    public async Task<Result<EntityDetail>> ExecuteAsync(string name, string alias, string domain, CancellationToken cancellationToken)
    {
        Result allowed = AdminAuthorization.RequireGlobal(actor.Current);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        string trimmedName = name?.Trim() ?? string.Empty;
        if (trimmedName.Length is 0 or > 100)
        {
            return Error.Validation("Enter a name of up to 100 characters.", "name");
        }

        string normalisedAlias = alias?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!AliasPattern().IsMatch(normalisedAlias))
        {
            return Error.Validation("The alias is 2–40 lower-case letters, digits or hyphens.", "alias");
        }

        if (!MailDomain.TryParse(domain, out MailDomain? mailDomain))
        {
            return Error.Validation("Enter a domain name such as example.com.", "domain");
        }

        Result<Entity> created = await entities.CreateAsync(new NewEntity(trimmedName, normalisedAlias, mailDomain), cancellationToken).ConfigureAwait(false);
        if (!created.IsSuccess)
        {
            Record(null, normalisedAlias, mailDomain, false);
            return created.Error;
        }

        DomainStatus status = await domains.EnsureAsync(created.Value, mailDomain, cancellationToken).ConfigureAwait(false);
        Record(created.Value.Id, normalisedAlias, mailDomain, true, status.Mail.State);
        return new EntityDetail(created.Value, [status]);
    }

    private void Record(string? entityId, string alias, MailDomain domain, bool succeeded, LinkState? mail = null) =>
        audit.Record(new AuditEvent("entity.created", actor.Current.UserId, succeeded)
        {
            EntityId = entityId,
            Details = new Dictionary<string, string?> { ["alias"] = alias, ["domain"] = domain.Name, ["mail"] = mail?.ToString() },
        });

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,38}[a-z0-9])$", RegexOptions.CultureInvariant)]
    private static partial Regex AliasPattern();
}
