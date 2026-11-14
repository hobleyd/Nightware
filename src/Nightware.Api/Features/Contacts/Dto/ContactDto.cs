using System.Text.Json.Serialization;

using Nightware.Api.Features.Mail.Dto;

namespace Nightware.Api.Features.Contacts.Dto;

/// <summary>Graph <c>contact</c>. NightMail selects only <c>displayName,emailAddresses</c>.</summary>
public sealed record ContactDto
{
    public required string Id { get; init; }

    public string? DisplayName { get; init; }

    public string? GivenName { get; init; }

    public string? Surname { get; init; }

    public string? CompanyName { get; init; }

    public string? JobTitle { get; init; }

    public IReadOnlyList<EmailAddressDto> EmailAddresses { get; init; } = [];

    public IReadOnlyList<string> BusinessPhones { get; init; } = [];

    public IReadOnlyList<string> HomePhones { get; init; } = [];

    public string? MobilePhone { get; init; }

    public PhysicalAddressDto? BusinessAddress { get; init; }

    public PhysicalAddressDto? HomeAddress { get; init; }

    /// <summary>Graph carries a birthday as midnight UTC.</summary>
    public DateTimeOffset? Birthday { get; init; }

    public string? PersonalNotes { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LastModifiedDateTime { get; init; }
}

/// <summary>Graph <c>physicalAddress</c>.</summary>
public sealed record PhysicalAddressDto
{
    public string? Street { get; init; }

    public string? City { get; init; }

    public string? State { get; init; }

    public string? PostalCode { get; init; }

    public string? CountryOrRegion { get; init; }
}
