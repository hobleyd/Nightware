namespace Nightware.Domain.Administration;

/// <summary>
/// An organisation that owns one or more mail domains (a Keycloak Organization, ADR 0002 D1).
/// <paramref name="Alias"/> is the stable handle shown in URLs.
/// </summary>
public sealed record Entity(string Id, string Alias, string Name, IReadOnlyList<MailDomain> Domains)
{
    /// <summary>Shared, so entities without choices still compare equal.</summary>
    private static readonly IReadOnlyDictionary<MailDomain, DomainMailPolicy> NoPolicies = new Dictionary<MailDomain, DomainMailPolicy>();

    private static readonly IReadOnlyDictionary<MailDomain, string> NoCatchAlls = new Dictionary<MailDomain, string>();

    /// <summary>
    /// The SPF and DMARC choices of domains not left at the mail server's default. Filled when one entity is
    /// read; a listing of entities may leave it empty.
    /// </summary>
    public IReadOnlyDictionary<MailDomain, DomainMailPolicy> MailPolicies { get; init; } = NoPolicies;

    /// <summary>
    /// The DNS host the entity has given the console a credential for, to publish its domains' records with; null
    /// when it has none. Filled when one entity is read, like <see cref="MailPolicies"/>.
    /// </summary>
    public DnsProvider? DnsHost { get; init; }

    /// <summary>Whether the entity has a DNS host credential (<see cref="DnsHost"/>).</summary>
    public bool HasDnsToken => DnsHost is not null;

    /// <summary>
    /// The user (by id) each domain's mail to unknown addresses goes to, for domains that have one; mail to an
    /// unknown address on any other domain is refused. Filled when one entity is read, like <see cref="MailPolicies"/>.
    /// </summary>
    public IReadOnlyDictionary<MailDomain, string> CatchAlls { get; init; } = NoCatchAlls;

    /// <summary>The id of the user who receives <paramref name="domain"/>'s mail to unknown addresses, or null when it is refused.</summary>
    public string? CatchAllFor(MailDomain domain) => CatchAlls.GetValueOrDefault(domain);

    /// <summary>The domains whose mail to unknown addresses goes to <paramref name="userId"/>.</summary>
    public IReadOnlyList<MailDomain> CatchAllsOf(string userId) =>
        [.. CatchAlls.Where(c => string.Equals(c.Value, userId, StringComparison.Ordinal)).Select(static c => c.Key).OrderBy(static d => d.Name, StringComparer.Ordinal)];

    public DomainMailPolicy MailPolicyFor(MailDomain domain) =>
        MailPolicies.TryGetValue(domain, out DomainMailPolicy? policy) ? policy : DomainMailPolicy.MailServerDefault;

    /// <summary>Whether <paramref name="address"/> is on one of this entity's domains.</summary>
    public bool Owns(MailAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return Domains.Contains(address.Domain);
    }
}

/// <summary>What the console asks for when an entity is created.</summary>
public sealed record NewEntity(string Name, string Alias, MailDomain FirstDomain);

/// <summary>An identity in the system of record (a Keycloak user), as the console sees it.</summary>
public sealed record IdentityAccount(string Id, string Username, string Email, bool Enabled)
{
    public string? FirstName { get; init; }

    public string? LastName { get; init; }

    /// <summary>Secondary addresses (Keycloak attribute <c>nightware_aliases</c>).</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>When the console deleted this user's mail (Keycloak attribute <c>nightware_mail_deleted_at</c>).</summary>
    public DateTimeOffset? MailDeletedAt { get; init; }

    /// <summary>When the console deleted this user's Nextcloud data.</summary>
    public DateTimeOffset? GroupwareDeletedAt { get; init; }

    /// <summary>What administrators did to this user's access, newest first (Keycloak attribute <c>nightware_access_history</c>).</summary>
    public IReadOnlyList<AccessEvent> AccessHistory { get; init; } = [];

    public string DisplayName => string.Join(' ', new[] { FirstName, LastName }.Where(static n => !string.IsNullOrWhiteSpace(n))) is { Length: > 0 } name
        ? name
        : Username;
}

/// <summary>A Keycloak user plus what the console knows about their entity and roles.</summary>
public sealed record ManagedUser(IdentityAccount Identity, string? EntityId, bool IsGlobalAdmin, bool IsEntityAdmin);

/// <summary>Keycloak required actions the console may ask for.</summary>
public enum RequiredAction
{
    UpdatePassword,
    ConfigureTotp,
}
