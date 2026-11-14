using Nightware.Domain.Directory;

namespace Nightware.Api.Features.Directory.Dto;

public static class UserDtoMapper
{
    /// <param name="select">The request's <c>$select</c>; decides which non-default members are filled.</param>
    public static UserDto ToDto(DirectoryUser user, IReadOnlySet<string>? select)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new UserDto
        {
            Id = select is null || select.Contains("id") ? user.Id : null,
            DisplayName = user.DisplayName,
            GivenName = user.GivenName,
            Surname = user.Surname,
            Mail = user.Mail,
            UserPrincipalName = user.UserPrincipalName,
            JobTitle = user.JobTitle,
            OfficeLocation = user.OfficeLocation,
            MobilePhone = user.MobilePhone,
            BusinessPhones = user.BusinessPhones,
            Department = Selected(select, "department") ? user.Department : null,
            CompanyName = Selected(select, "companyName") ? user.CompanyName : null,
            ProxyAddresses = Selected(select, "proxyAddresses") ? ProxyAddresses(user) : null,
        };
    }

    /// <summary>Exchange-style proxy addresses: <c>SMTP:</c> marks the primary address, <c>smtp:</c> the aliases.</summary>
    public static IReadOnlyList<string> ProxyAddresses(DirectoryUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(user.Mail) && seen.Add(user.Mail))
        {
            result.Add("SMTP:" + user.Mail);
        }

        foreach (var alias in user.Aliases.Where(static a => !string.IsNullOrWhiteSpace(a)))
        {
            if (seen.Add(alias))
            {
                result.Add("smtp:" + alias);
            }
        }

        result.AddRange(user.ProxyAddresses.Where(static p => !p.StartsWith("smtp:", StringComparison.OrdinalIgnoreCase)));
        return result;
    }

    private static bool Selected(IReadOnlySet<string>? select, string property) => select?.Contains(property) == true;
}
