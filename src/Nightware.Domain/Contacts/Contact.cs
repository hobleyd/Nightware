using Nightware.Domain.Kernel;

namespace Nightware.Domain.Contacts;

public sealed record PhysicalAddress(string? Street, string? City, string? State, string? PostalCode, string? CountryOrRegion)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Street) && string.IsNullOrWhiteSpace(City) && string.IsNullOrWhiteSpace(State)
        && string.IsNullOrWhiteSpace(PostalCode) && string.IsNullOrWhiteSpace(CountryOrRegion);
}

/// <summary>A personal contact (Graph <c>contact</c>), backed by a vCard.</summary>
public sealed record Contact(ContactId Id, string? DisplayName)
{
    public string? GivenName { get; init; }

    public string? MiddleName { get; init; }

    public string? Surname { get; init; }

    /// <summary>Graph <c>title</c>: the honorific (Dr, Ms), not the job title.</summary>
    public string? Title { get; init; }

    /// <summary>Graph <c>generation</c>: the suffix (Jr, III).</summary>
    public string? Generation { get; init; }

    public string? NickName { get; init; }

    public string? Initials { get; init; }

    public string? FileAs { get; init; }

    public string? YomiGivenName { get; init; }

    public string? YomiSurname { get; init; }

    public string? YomiCompanyName { get; init; }

    public string? CompanyName { get; init; }

    public string? Department { get; init; }

    public string? JobTitle { get; init; }

    public string? Profession { get; init; }

    public string? OfficeLocation { get; init; }

    public string? AssistantName { get; init; }

    public string? Manager { get; init; }

    public string? SpouseName { get; init; }

    public string? BusinessHomePage { get; init; }

    public string? MobilePhone { get; init; }

    public PhysicalAddress? BusinessAddress { get; init; }

    public PhysicalAddress? HomeAddress { get; init; }

    public PhysicalAddress? OtherAddress { get; init; }

    public string? PersonalNotes { get; init; }

    public IReadOnlyList<EmailAddress> EmailAddresses { get; init; } = [];

    public IReadOnlyList<string> BusinessPhones { get; init; } = [];

    public IReadOnlyList<string> HomePhones { get; init; } = [];

    public IReadOnlyList<string> ImAddresses { get; init; } = [];

    public IReadOnlyList<string> Children { get; init; } = [];

    public IReadOnlyList<string> Categories { get; init; } = [];

    public DateOnly? Birthday { get; init; }

    public DateTimeOffset? CreatedDateTime { get; init; }

    public DateTimeOffset? LastModifiedDateTime { get; init; }

    /// <summary>The folder (address book) holding the contact; set by the store.</summary>
    public ContactFolderId? ParentFolderId { get; init; }

    /// <summary>Changes whenever the contact does (the resource's ETag); set by the store.</summary>
    public string? ChangeKey { get; init; }
}

/// <summary>
/// Contact create/patch payload. A null member is left as it is; an empty string or list clears it, as
/// does an address with every part empty. <see cref="ClearBirthday"/> clears the birthday.
/// </summary>
public sealed record ContactDraft
{
    public string? DisplayName { get; init; }

    public string? GivenName { get; init; }

    public string? MiddleName { get; init; }

    public string? Surname { get; init; }

    /// <summary>Graph <c>title</c>: the honorific (Dr, Ms), not the job title.</summary>
    public string? Title { get; init; }

    /// <summary>Graph <c>generation</c>: the suffix (Jr, III).</summary>
    public string? Generation { get; init; }

    public string? NickName { get; init; }

    public string? Initials { get; init; }

    public string? FileAs { get; init; }

    public string? YomiGivenName { get; init; }

    public string? YomiSurname { get; init; }

    public string? YomiCompanyName { get; init; }

    public string? CompanyName { get; init; }

    public string? Department { get; init; }

    public string? JobTitle { get; init; }

    public string? Profession { get; init; }

    public string? OfficeLocation { get; init; }

    public string? AssistantName { get; init; }

    public string? Manager { get; init; }

    public string? SpouseName { get; init; }

    public string? BusinessHomePage { get; init; }

    public string? MobilePhone { get; init; }

    public PhysicalAddress? BusinessAddress { get; init; }

    public PhysicalAddress? HomeAddress { get; init; }

    public PhysicalAddress? OtherAddress { get; init; }

    public string? PersonalNotes { get; init; }

    public IReadOnlyList<EmailAddress>? EmailAddresses { get; init; }

    public IReadOnlyList<string>? BusinessPhones { get; init; }

    public IReadOnlyList<string>? HomePhones { get; init; }

    public IReadOnlyList<string>? ImAddresses { get; init; }

    public IReadOnlyList<string>? Children { get; init; }

    public IReadOnlyList<string>? Categories { get; init; }

    public DateOnly? Birthday { get; init; }

    public bool ClearBirthday { get; init; }
}

/// <summary>A contact folder (Graph <c>contactFolder</c>): a CardDAV address book.</summary>
public sealed record ContactFolder(ContactFolderId Id, string DisplayName, ContactFolderId? ParentFolderId);

/// <summary>A ranked person relevant to the user (Graph <c>person</c>).</summary>
public sealed record Person(string Id, string DisplayName, IReadOnlyList<EmailAddress> ScoredEmailAddresses)
{
    public string? GivenName { get; init; }

    public string? Surname { get; init; }

    public string? CompanyName { get; init; }

    public string? JobTitle { get; init; }

    /// <summary>
    /// Relevance score on Graph's open-ended scale (Graph emits values such as <c>20.0</c>); higher is
    /// more relevant. Only the order between people is meaningful.
    /// </summary>
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
