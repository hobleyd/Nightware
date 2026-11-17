using System.Text.Json.Nodes;

using Nightware.Adapters.Directory.Core.Admin;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Administration;

/// <summary>
/// <see cref="IEntityRegistry"/> over Keycloak Organizations (ADR 0002 D1, D4): entity = organization,
/// domains = its <c>domains[]</c>, user ∈ entity = unmanaged membership, entity admins via
/// <see cref="KeycloakEntityAdmins"/>.
/// </summary>
internal sealed class KeycloakEntityRegistry(IKeycloakAdminClient keycloak, KeycloakEntityAdmins admins) : IEntityRegistry
{
    public async Task<Result<IReadOnlyList<Entity>>> ListAsync(CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<KeycloakOrganization>> organizations = await keycloak.ListOrganizationsAsync(cancellationToken).ConfigureAwait(false);
        return organizations.Map(static IReadOnlyList<Entity> (orgs) => [.. orgs.Select(KeycloakAdministrationMapper.ToEntity).OrderBy(static e => e.Name, StringComparer.CurrentCultureIgnoreCase)]);
    }

    public async Task<Result<Entity>> GetAsync(string entityId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        Result<KeycloakOrganization> organization = await keycloak.GetOrganizationAsync(entityId, cancellationToken).ConfigureAwait(false);
        return organization.Map(KeycloakAdministrationMapper.ToEntity);
    }

    public async Task<Result<Entity>> CreateAsync(NewEntity entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);

        // Keycloak refuses a domain another organization holds with a 400; the port promises Conflict.
        Result owner = await RequireDomainFreeAsync(entity.FirstDomain, exceptOrganizationId: null, cancellationToken).ConfigureAwait(false);
        if (!owner.IsSuccess)
        {
            return owner.Error;
        }

        KeycloakOrganization organization = new()
        {
            Name = entity.Name,
            Alias = entity.Alias,
            Enabled = true,
            Domains = [new KeycloakOrganizationDomain { Name = entity.FirstDomain.Name }],
        };
        Result<string> id = await keycloak.CreateOrganizationAsync(organization, cancellationToken).ConfigureAwait(false);
        return id.IsSuccess
            ? new Entity(id.Value, entity.Alias, entity.Name, [entity.FirstDomain])
            : DomainClash(id.Error, entity.FirstDomain);
    }

    public async Task<Result<Entity>> AddDomainAsync(string entityId, MailDomain domain, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentNullException.ThrowIfNull(domain);

        Result owner = await RequireDomainFreeAsync(domain, entityId, cancellationToken).ConfigureAwait(false);
        if (!owner.IsSuccess)
        {
            return owner.Error;
        }

        Result<KeycloakOrganization> updated = await keycloak.UpdateOrganizationAsync(
                entityId,
                representation =>
                {
                    JsonArray domains = DomainsOf(representation);
                    if (IndexOf(domains, domain) >= 0)
                    {
                        return false;
                    }

                    domains.Add(new JsonObject { ["name"] = domain.Name, ["verified"] = false });
                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
        return updated.IsSuccess ? KeycloakAdministrationMapper.ToEntity(updated.Value) : DomainClash(updated.Error, domain);
    }

    public async Task<Result<Entity>> RemoveDomainAsync(string entityId, MailDomain domain, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentNullException.ThrowIfNull(domain);

        Result<KeycloakOrganization> updated = await keycloak.UpdateOrganizationAsync(
                entityId,
                representation =>
                {
                    JsonArray domains = DomainsOf(representation);
                    int index = IndexOf(domains, domain);
                    if (index < 0)
                    {
                        return false;
                    }

                    domains.RemoveAt(index);
                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
        return updated.Map(KeycloakAdministrationMapper.ToEntity);
    }

    public async Task<Result<IReadOnlyList<IdentityAccount>>> ListMembersAsync(string entityId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        Result<IReadOnlyList<KeycloakUser>> members = await keycloak.ListOrganizationMembersAsync(entityId, cancellationToken).ConfigureAwait(false);
        return members.Map(KeycloakAdministrationMapper.ToIdentities);
    }

    public async Task<Result<IReadOnlyList<string>>> GetMembershipsAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        Result<IReadOnlyList<KeycloakOrganization>> organizations = await keycloak.ListMemberOrganizationsAsync(userId, cancellationToken).ConfigureAwait(false);
        return organizations.Map(static IReadOnlyList<string> (orgs) => [.. orgs.Select(static o => o.Id).OfType<string>()]);
    }

    public Task<Result> AddMemberAsync(string entityId, string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return keycloak.AddOrganizationMemberAsync(entityId, userId, cancellationToken);
    }

    public async Task<Result<IReadOnlyList<IdentityAccount>>> ListAdminsAsync(string entityId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        Result<IReadOnlyList<KeycloakUser>> users = await admins.ListAsync(entityId, cancellationToken).ConfigureAwait(false);
        return users.Map(KeycloakAdministrationMapper.ToIdentities);
    }

    public Task<Result> GrantAdminAsync(string entityId, string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return admins.GrantAsync(entityId, userId, cancellationToken);
    }

    public Task<Result> RevokeAdminAsync(string entityId, string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return admins.RevokeAsync(entityId, userId, cancellationToken);
    }

    /// <summary>Conflict when an organization other than <paramref name="exceptOrganizationId"/> holds the domain.</summary>
    private async Task<Result> RequireDomainFreeAsync(MailDomain domain, string? exceptOrganizationId, CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<KeycloakOrganization>> found = await keycloak.SearchOrganizationsAsync(domain.Name, exact: true, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            return found.Error;
        }

        KeycloakOrganization? owner = found.Value.FirstOrDefault(o =>
            !string.Equals(o.Id, exceptOrganizationId, StringComparison.Ordinal)
            && (o.Domains ?? []).Any(d => string.Equals(d.Name, domain.Name, StringComparison.OrdinalIgnoreCase)));
        return owner is null
            ? Result.Success()
            : Error.Conflict($"The domain {domain} already belongs to the entity '{owner.Name}'.", "domain");
    }

    /// <summary>The pre-check can lose a race; Keycloak then answers 400 "Domain … is already linked to organization …".</summary>
    private static Error DomainClash(Error error, MailDomain domain) =>
        error.Kind == ErrorKind.Validation && error.Message.Contains("already linked", StringComparison.OrdinalIgnoreCase)
            ? Error.Conflict($"The domain {domain} already belongs to another entity.", "domain")
            : error;

    private static JsonArray DomainsOf(JsonObject representation)
    {
        if (representation["domains"] is JsonArray domains)
        {
            return domains;
        }

        JsonArray created = [];
        representation["domains"] = created;
        return created;
    }

    private static int IndexOf(JsonArray domains, MailDomain domain)
    {
        for (int i = 0; i < domains.Count; i++)
        {
            if (domains[i] is JsonObject entry
                && entry["name"] is JsonValue name
                && name.TryGetValue(out string? value)
                && string.Equals(value, domain.Name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
