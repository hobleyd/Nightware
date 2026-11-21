using System.Text.Json.Serialization;

using Nightware.Api.Features.Mail.Dto;

namespace Nightware.Api.Features.Contacts.Dto;

/// <summary>Graph <c>contact</c>, in Graph's member order. NightMail selects only <c>displayName,emailAddresses</c>.</summary>
public sealed record ContactDto
{
    public required string Id { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? CreatedDateTime { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LastModifiedDateTime { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChangeKey { get; init; }

    public IReadOnlyList<string> Categories { get; init; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentFolderId { get; init; }

    /// <summary>Exchange stores a birthday at 11:59 UTC on the day, so no time zone moves it to another day.</summary>
    public DateTimeOffset? Birthday { get; init; }

    public string FileAs { get; init; } = string.Empty;

    public string? DisplayName { get; init; }

    public string? GivenName { get; init; }

    public string? Initials { get; init; }

    public string? MiddleName { get; init; }

    public string? NickName { get; init; }

    public string? Surname { get; init; }

    public string? Title { get; init; }

    public string? YomiGivenName { get; init; }

    public string? YomiSurname { get; init; }

    public string? YomiCompanyName { get; init; }

    public string? Generation { get; init; }

    public IReadOnlyList<string> ImAddresses { get; init; } = [];

    public string? JobTitle { get; init; }

    public string? CompanyName { get; init; }

    public string? Department { get; init; }

    public string? OfficeLocation { get; init; }

    public string? Profession { get; init; }

    public string? BusinessHomePage { get; init; }

    public string? AssistantName { get; init; }

    public string? Manager { get; init; }

    public IReadOnlyList<string> HomePhones { get; init; } = [];

    public string? MobilePhone { get; init; }

    public IReadOnlyList<string> BusinessPhones { get; init; } = [];

    public string? SpouseName { get; init; }

    public string PersonalNotes { get; init; } = string.Empty;

    public IReadOnlyList<string> Children { get; init; } = [];

    public IReadOnlyList<EmailAddressDto> EmailAddresses { get; init; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EmailAddressDto? PrimaryEmailAddress { get; init; }

    public PhysicalAddressDto HomeAddress { get; init; } = new();

    public PhysicalAddressDto BusinessAddress { get; init; } = new();

    public PhysicalAddressDto OtherAddress { get; init; } = new();
}

/// <summary>Graph <c>physicalAddress</c>; an empty address is <c>{}</c>, as Graph writes it.</summary>
public sealed record PhysicalAddressDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Street { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? City { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? State { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CountryOrRegion { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PostalCode { get; init; }
}
