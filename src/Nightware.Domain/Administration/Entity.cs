namespace Nightware.Domain.Administration;

/// <summary>
/// An organisation that owns one or more mail domains (a Keycloak Organization, ADR 0002 D1).
/// <paramref name="Alias"/> is the stable handle shown in URLs.
/// </summary>
public sealed record Entity(string Id, string Alias, string Name, IReadOnlyList<MailDomain> Domains)
{
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
