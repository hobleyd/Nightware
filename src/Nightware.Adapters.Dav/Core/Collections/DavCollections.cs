namespace Nightware.Adapters.Dav.Core.Collections;

/// <summary>A calendar collection (events and/or tasks).</summary>
/// <param name="Href">Server-absolute href with trailing slash.</param>
public sealed record DavCalendar(string Href, string DisplayName)
{
    /// <summary>Component names from <c>supported-calendar-component-set</c> (upper case). Empty means "any".</summary>
    public IReadOnlySet<string> Components { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public string? Description { get; init; }

    /// <summary><c>#RRGGBB</c> (Nextcloud may append alpha: <c>#RRGGBBAA</c>).</summary>
    public string? Color { get; init; }

    public string? CTag { get; init; }

    public string? SyncToken { get; init; }

    public int? Order { get; init; }

    /// <summary>Owner principal href (Nextcloud <c>oc:owner-principal</c>), e.g. <c>principals/users/bob</c>.</summary>
    public string? OwnerPrincipal { get; init; }

    public string? OwnerDisplayName { get; init; }

    /// <summary>Shared with the caller by someone else.</summary>
    public bool IsShared { get; init; }

    public bool IsReadOnly { get; init; }

    public bool SupportsEvents => Components.Count == 0 || Components.Contains("VEVENT");

    public bool SupportsTasks => Components.Count == 0 || Components.Contains("VTODO");
}

/// <summary>A CardDAV address book.</summary>
public sealed record DavAddressBook(string Href, string DisplayName)
{
    public string? Description { get; init; }

    public string? CTag { get; init; }

    public string? SyncToken { get; init; }

    public string? OwnerPrincipal { get; init; }

    public bool IsShared { get; init; }

    public bool IsReadOnly { get; init; }
}
