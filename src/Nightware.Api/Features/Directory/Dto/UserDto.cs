using System.Text.Json.Serialization;

namespace Nightware.Api.Features.Directory.Dto;

/// <summary>
/// Graph <c>user</c>, trimmed to what NightMail selects (odata-subset.md §2). Members outside Graph's
/// default user projection (<c>department</c>, <c>companyName</c>, <c>proxyAddresses</c>) and, under a
/// <c>$select</c> without it, <c>id</c> are null and omitted — Graph returns directory objects with
/// exactly the selected properties, which the golden fixtures pin.
/// </summary>
public record UserDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }

    public string? DisplayName { get; init; }

    public string? GivenName { get; init; }

    public string? Surname { get; init; }

    public string? Mail { get; init; }

    public string? UserPrincipalName { get; init; }

    public string? JobTitle { get; init; }

    public string? OfficeLocation { get; init; }

    public string? MobilePhone { get; init; }

    public IReadOnlyList<string> BusinessPhones { get; init; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Department { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CompanyName { get; init; }

    /// <summary><c>SMTP:</c> primary, <c>smtp:</c> aliases, then other typed addresses verbatim.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ProxyAddresses { get; init; }
}
