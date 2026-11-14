namespace Nightware.Api.Features.People.Dto;

/// <summary>Graph <c>person</c>. NightMail selects only <c>displayName,scoredEmailAddresses</c>.</summary>
public sealed record PersonDto
{
    public required string Id { get; init; }

    public string? DisplayName { get; init; }

    public string? GivenName { get; init; }

    public string? Surname { get; init; }

    public string? CompanyName { get; init; }

    public string? JobTitle { get; init; }

    public IReadOnlyList<ScoredEmailAddressDto> ScoredEmailAddresses { get; init; } = [];
}

/// <summary>Graph <c>scoredEmailAddress</c>.</summary>
public sealed record ScoredEmailAddressDto
{
    public required string Address { get; init; }

    public double RelevanceScore { get; init; }

    /// <summary>Graph's <c>selectionLikelihoodInfo</c>; Nightware has no selection model.</summary>
    public string SelectionLikelihood { get; init; } = "notSpecified";
}
