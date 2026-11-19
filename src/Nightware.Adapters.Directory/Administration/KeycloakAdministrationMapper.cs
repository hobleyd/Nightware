using System.Globalization;

using Nightware.Adapters.Directory.Core.Admin;
using Nightware.Domain.Administration;

namespace Nightware.Adapters.Directory.Administration;

/// <summary>Keycloak representations → the console's Domain types.</summary>
internal static class KeycloakAdministrationMapper
{
    public static IdentityAccount ToIdentity(KeycloakUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new IdentityAccount(user.Id, user.Username ?? string.Empty, user.Email ?? string.Empty, user.Enabled)
        {
            FirstName = Blank(user.FirstName),
            LastName = Blank(user.LastName),
            Aliases = Values(user, KeycloakAdminNames.AliasesAttribute),
            MailDeletedAt = Marker(user.Attribute(KeycloakAdminNames.MailDeletedAtAttribute)),
            GroupwareDeletedAt = Marker(user.Attribute(KeycloakAdminNames.GroupwareDeletedAtAttribute)),
            AccessHistory = History(Values(user, KeycloakAdminNames.AccessHistoryAttribute)),
        };
    }

    public static IReadOnlyList<IdentityAccount> ToIdentities(IEnumerable<KeycloakUser> users) => [.. users.Select(ToIdentity)];

    /// <summary>
    /// Domains that are not valid <see cref="MailDomain"/>s (set by hand in Keycloak's console) are left out:
    /// the console cannot assign addresses on them anyway.
    /// </summary>
    public static Entity ToEntity(KeycloakOrganization organization)
    {
        ArgumentNullException.ThrowIfNull(organization);
        MailDomain[] domains =
        [
            .. (organization.Domains ?? [])
                .Select(static d => MailDomain.TryParse(d.Name, out MailDomain? domain) ? domain : null)
                .OfType<MailDomain>()
                .Distinct()
                .OrderBy(static d => d.Name, StringComparer.Ordinal),
        ];
        Dictionary<MailDomain, DomainMailPolicy> policies = [];
        foreach (MailDomain domain in domains)
        {
            DomainMailPolicy policy = MailPolicy(organization, domain);
            if (!policy.IsDefault)
            {
                policies[domain] = policy;
            }
        }

        Entity entity = new(organization.Id ?? string.Empty, organization.Alias ?? string.Empty, organization.Name, domains)
        {
            DnsHost = KeycloakAdminNames.DnsToken(organization)?.Provider,
        };
        return policies.Count == 0 ? entity : entity with { MailPolicies = policies };
    }

    /// <summary>A record edited by hand in Keycloak's console that no longer parses counts as the mail server's default.</summary>
    private static DomainMailPolicy MailPolicy(KeycloakOrganization organization, MailDomain domain) =>
        new(
            organization.Attribute(KeycloakAdminNames.SpfAttribute(domain)) is { } spf && SpfRecord.Parse(spf) is { IsSuccess: true } parsedSpf ? parsedSpf.Value : null,
            organization.Attribute(KeycloakAdminNames.DmarcAttribute(domain)) is { } dmarc && DmarcRecord.Parse(dmarc) is { IsSuccess: true } parsedDmarc ? parsedDmarc.Value : null);

    /// <summary>The marker format: ISO-8601 round-trip, always UTC.</summary>
    public static string FormatMarker(DateTimeOffset at) => at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>A malformed marker (edited by hand) reads as absent rather than failing the user.</summary>
    public static DateTimeOffset? Marker(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset at) ? at : null;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static IReadOnlyList<string> Values(KeycloakUser user, string name) =>
        user.Attributes is not null && user.Attributes.TryGetValue(name, out IReadOnlyList<string>? values)
            ? [.. values.Where(static v => !string.IsNullOrWhiteSpace(v))]
            : [];

    /// <summary>The readable history lines, newest first; the shared empty list when there are none.</summary>
    private static AccessEvent[] History(IReadOnlyList<string> lines)
    {
        AccessEvent[] events = [.. lines.Select(AccessEvent.Parse).OfType<AccessEvent>().OrderByDescending(static e => e.At)];
        return events.Length == 0 ? Array.Empty<AccessEvent>() : events;
    }
}
