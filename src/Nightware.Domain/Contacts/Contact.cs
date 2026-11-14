using Nightware.Domain.Kernel;

namespace Nightware.Domain.Contacts;

public sealed record PhysicalAddress(string? Street, string? City, string? State, string? PostalCode, string? CountryOrRegion);

/// <summary>A personal contact (Graph <c>contact</c>), backed by a vCard.</summary>
public sealed record Contact(ContactId Id, string? DisplayName)
{
    public string? GivenName { get; init; }

    public string? Surname { get; init; }

    public string? CompanyName { get; init; }

    public string? JobTitle { get; init; }

    public IReadOnlyList<EmailAddress> EmailAddresses { get; init; } = [];

    public IReadOnlyList<string> BusinessPhones { get; init; } = [];

    public IReadOnlyList<string> HomePhones { get; init; } = [];

    public string? MobilePhone { get; init; }

    public PhysicalAddress? BusinessAddress { get; init; }

    public PhysicalAddress? HomeAddress { get; init; }

    public DateOnly? Birthday { get; init; }

    public string? PersonalNotes { get; init; }

    public DateTimeOffset? LastModifiedDateTime { get; init; }
}

/// <summary>Contact create/replace payload; the vCard is regenerated from it.</summary>
public sealed record ContactDraft
{
    public string? DisplayName { get; init; }

    public string? GivenName { get; init; }

    public string? Surname { get; init; }

    public string? CompanyName { get; init; }

    public string? JobTitle { get; init; }

    public IReadOnlyList<EmailAddress>? EmailAddresses { get; init; }

    public IReadOnlyList<string>? BusinessPhones { get; init; }

    public IReadOnlyList<string>? HomePhones { get; init; }

    public string? MobilePhone { get; init; }

    public PhysicalAddress? BusinessAddress { get; init; }

    public PhysicalAddress? HomeAddress { get; init; }

    public DateOnly? Birthday { get; init; }

    public string? PersonalNotes { get; init; }
}

/// <summary>A ranked person relevant to the user (Graph <c>person</c>).</summary>
public sealed record Person(string Id, string DisplayName, IReadOnlyList<EmailAddress> ScoredEmailAddresses)
{
    public string? GivenName { get; init; }

    public string? Surname { get; init; }

    public string? CompanyName { get; init; }

    public string? JobTitle { get; init; }

    /// <summary>Relevance score in [0, 1]; higher is more relevant.</summary>
    public double RelevanceScore { get; init; }

    public PersonSource Source { get; init; }
}

[Flags]
public enum PersonSource
{
    None = 0,
    Contact = 1,
    Directory = 2,
    Correspondent = 4,
}
