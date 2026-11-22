using System.Text.Json.Serialization;

using Nightware.Domain.Directory;

namespace Nightware.Api.Features.Directory.Dto;

/// <summary>
/// A user inside a <c>directoryObject</c> collection or relationship (<c>manager</c>, <c>directReports</c>,
/// <c>members</c>): Graph leads it with its derived type, since the collection's own type is the base.
/// </summary>
public sealed record UserObjectDto : UserDto
{
    public const string ODataTypeName = "#microsoft.graph.user";

    [JsonPropertyName("@odata.type")]
    [JsonPropertyOrder(-1)]
    public string ODataType { get; init; } = ODataTypeName;

    public static UserObjectDto From(DirectoryUser user, IReadOnlySet<string>? select)
    {
        var dto = UserDtoMapper.ToDto(user, select);
        return new UserObjectDto
        {
            Id = dto.Id,
            DisplayName = dto.DisplayName,
            GivenName = dto.GivenName,
            Surname = dto.Surname,
            Mail = dto.Mail,
            UserPrincipalName = dto.UserPrincipalName,
            JobTitle = dto.JobTitle,
            OfficeLocation = dto.OfficeLocation,
            MobilePhone = dto.MobilePhone,
            BusinessPhones = dto.BusinessPhones,
            Department = dto.Department,
            CompanyName = dto.CompanyName,
            ProxyAddresses = dto.ProxyAddresses,
        };
    }
}

/// <summary>
/// Graph <c>group</c> with its default properties (group-get). A group's list makes it mail-enabled (a distribution
/// group, <c>groupTypes</c> empty) and its files facet security-enabled; what Nightware doesn't keep is null.
/// </summary>
public record GroupDto
{
    public required string Id { get; init; }

    public DateTimeOffset? DeletedDateTime { get; init; }

    public string? Classification { get; init; }

    public DateTimeOffset? CreatedDateTime { get; init; }

    public string? Description { get; init; }

    public required string DisplayName { get; init; }

    public DateTimeOffset? ExpirationDateTime { get; init; }

    public IReadOnlyList<string> GroupTypes { get; init; } = [];

    public bool? IsAssignableToRole { get; init; }

    public string? Mail { get; init; }

    public bool MailEnabled { get; init; }

    public required string MailNickname { get; init; }

    public string? MembershipRule { get; init; }

    public string? MembershipRuleProcessingState { get; init; }

    public DateTimeOffset? OnPremisesLastSyncDateTime { get; init; }

    public string? OnPremisesSecurityIdentifier { get; init; }

    public bool? OnPremisesSyncEnabled { get; init; }

    public string? PreferredDataLocation { get; init; }

    public string? PreferredLanguage { get; init; }

    /// <summary><c>SMTP:</c> the list's address, <c>smtp:</c> its aliases.</summary>
    public IReadOnlyList<string> ProxyAddresses { get; init; } = [];

    public DateTimeOffset? RenewedDateTime { get; init; }

    public IReadOnlyList<string> ResourceBehaviorOptions { get; init; } = [];

    public IReadOnlyList<string> ResourceProvisioningOptions { get; init; } = [];

    public bool SecurityEnabled { get; init; }

    public string? Theme { get; init; }

    public string? Visibility { get; init; }

    public IReadOnlyList<object> OnPremisesProvisioningErrors { get; init; } = [];

    public static GroupDto From(DirectoryGroup group) => Fill(new GroupDto { Id = group.Id, DisplayName = group.DisplayName, MailNickname = group.MailNickname }, group);

    protected static T Fill<T>(T dto, DirectoryGroup group)
        where T : GroupDto
    {
        ArgumentNullException.ThrowIfNull(group);
        return dto with
        {
            Mail = group.Mail,
            MailEnabled = group.MailEnabled,
            SecurityEnabled = group.SecurityEnabled,
            ProxyAddresses = group.Mail is null ? [] : [.. group.Aliases.Select(static a => "smtp:" + a).Prepend("SMTP:" + group.Mail)],
        };
    }
}

/// <summary>A group inside a <c>directoryObject</c> collection (<c>memberOf</c>), led by its derived type.</summary>
public sealed record GroupObjectDto : GroupDto
{
    public const string ODataTypeName = "#microsoft.graph.group";

    [JsonPropertyName("@odata.type")]
    [JsonPropertyOrder(-1)]
    public string ODataType { get; init; } = ODataTypeName;

    public static new GroupObjectDto From(DirectoryGroup group) =>
        Fill(new GroupObjectDto { Id = group.Id, DisplayName = group.DisplayName, MailNickname = group.MailNickname }, group);
}

/// <summary>
/// Graph <c>organization</c>. A signed-in user (User.Read) sees only <c>id</c>, <c>displayName</c> and
/// <c>verifiedDomains</c> in Graph, the rest null; Nightware keeps nothing more, so the rest is null or empty here too.
/// </summary>
public sealed record OrganizationDto
{
    public required string Id { get; init; }

    public DateTimeOffset? DeletedDateTime { get; init; }

    public IReadOnlyList<string> BusinessPhones { get; init; } = [];

    public string? City { get; init; }

    public string? Country { get; init; }

    public string? CountryLetterCode { get; init; }

    public DateTimeOffset? CreatedDateTime { get; init; }

    public string? DefaultUsageLocation { get; init; }

    public required string DisplayName { get; init; }

    public bool? IsMultipleDataLocationsForServicesEnabled { get; init; }

    public IReadOnlyList<string> MarketingNotificationEmails { get; init; } = [];

    public DateTimeOffset? OnPremisesLastSyncDateTime { get; init; }

    public bool? OnPremisesSyncEnabled { get; init; }

    public string? PartnerTenantType { get; init; }

    public string? PostalCode { get; init; }

    public string? PreferredLanguage { get; init; }

    public IReadOnlyList<string> SecurityComplianceNotificationMails { get; init; } = [];

    public IReadOnlyList<string> SecurityComplianceNotificationPhones { get; init; } = [];

    public string? State { get; init; }

    public string? Street { get; init; }

    public IReadOnlyList<string> TechnicalNotificationMails { get; init; } = [];

    public string TenantType { get; init; } = "AAD";

    public IReadOnlyList<object> AssignedPlans { get; init; } = [];

    public IReadOnlyList<object> ProvisionedPlans { get; init; } = [];

    public required IReadOnlyList<VerifiedDomainDto> VerifiedDomains { get; init; }

    /// <summary>The first domain is the default (and stands in for Graph's initial domain).</summary>
    public static OrganizationDto From(DirectoryOrganization organization)
    {
        ArgumentNullException.ThrowIfNull(organization);
        return new OrganizationDto
        {
            Id = organization.Id,
            DisplayName = organization.DisplayName,
            VerifiedDomains = [.. organization.Domains.Select(static (d, i) => new VerifiedDomainDto("Email", i == 0, i == 0, d, "Managed"))],
        };
    }
}

public sealed record VerifiedDomainDto(string Capabilities, bool IsDefault, bool IsInitial, string Name, string Type);
