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
    /// <summary>Keycloak's column for an attribute value (<c>GROUP_ATTRIBUTE.VALUE</c>, organizations being groups underneath).</summary>
    internal const int MaxAttributeLength = 255;

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
                    SetAttribute(representation, KeycloakAdminNames.SpfAttribute(domain), null);
                    SetAttribute(representation, KeycloakAdminNames.DmarcAttribute(domain), null);
                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
        return updated.Map(KeycloakAdministrationMapper.ToEntity);
    }

    public async Task<Result<Entity>> SetMailPolicyAsync(string entityId, MailDomain domain, DomainMailPolicy policy, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(policy);

        bool owned = false;
        Result<KeycloakOrganization> updated = await keycloak.UpdateOrganizationAsync(
                entityId,
                representation =>
                {
                    owned = IndexOf(DomainsOf(representation), domain) >= 0;
                    if (!owned)
                    {
                        return false;
                    }

                    bool spf = SetAttribute(representation, KeycloakAdminNames.SpfAttribute(domain), policy.Spf?.Value);
                    bool dmarc = SetAttribute(representation, KeycloakAdminNames.DmarcAttribute(domain), policy.Dmarc?.Value);
                    return spf || dmarc;
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (!updated.IsSuccess)
        {
            return updated.Error;
        }

        return owned ? KeycloakAdministrationMapper.ToEntity(updated.Value) : Error.NotFound($"The entity has no domain {domain}.", "domain");
    }

    public async Task<Result<SealedDnsToken?>> GetSealedDnsTokenAsync(string entityId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        Result<KeycloakOrganization> organization = await keycloak.GetOrganizationAsync(entityId, cancellationToken).ConfigureAwait(false);
        return organization.Map(KeycloakAdminNames.DnsToken);
    }

    public async Task<Result> SetSealedDnsTokenAsync(string entityId, SealedDnsToken? token, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        if (token is { Sealed.Length: > MaxAttributeLength })
        {
            // Keycloak stores an attribute value in 255 characters and answers a longer one with a 500.
            return Error.Validation("That token is too long to store.", "dnsToken");
        }

        Result<KeycloakOrganization> updated = await keycloak.UpdateOrganizationAsync(
                entityId,
                representation =>
                {
                    // One host at a time: every other provider's credential goes.
                    bool changed = false;
                    foreach (DnsProvider provider in DnsProviders.All)
                    {
                        changed |= SetAttribute(representation, KeycloakAdminNames.DnsTokenAttribute(provider), provider == token?.Provider ? token.Sealed : null);
                    }

                    return changed;
                },
                cancellationToken)
            .ConfigureAwait(false);
        return updated.IsSuccess ? Result.Success() : updated.Error;
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

    /// <summary>Sets a single-valued attribute, or removes it when <paramref name="value"/> is null; whether anything changed.</summary>
    private static bool SetAttribute(JsonObject representation, string name, string? value)
    {
        JsonObject? attributes = representation["attributes"] as JsonObject;
        string? current = attributes?[name] is JsonArray { Count: > 0 } values && values[0] is JsonValue first && first.TryGetValue(out string? text) ? text : null;
        if (value is null)
        {
            return attributes?.Remove(name) == true;
        }

        if (string.Equals(current, value, StringComparison.Ordinal))
        {
            return false;
        }

        if (attributes is null)
        {
            attributes = [];
            representation["attributes"] = attributes;
        }

        attributes[name] = new JsonArray(value);
        return true;
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
