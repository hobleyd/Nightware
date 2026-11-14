using Nightware.Adapters.Directory.Core.Admin;
using Nightware.Domain.Directory;

namespace Nightware.Adapters.Directory.Directory;

/// <summary>Keycloak <c>UserRepresentation</c> → <see cref="DirectoryUser"/>.</summary>
internal static class KeycloakUserMapper
{
    /// <summary>Keycloak's username prefix for a client's service-account user; never a person.</summary>
    public const string ServiceAccountPrefix = "service-account-";

    public static bool IsServiceAccount(KeycloakUser user) =>
        user.Username?.StartsWith(ServiceAccountPrefix, StringComparison.Ordinal) == true;

    public static DirectoryUser ToDirectoryUser(KeycloakUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var mail = NullIfBlank(user.Email);
        var username = NullIfBlank(user.Username);

        // Graph's userPrincipalName is always an address and the client uses it as one (mail ?? upn), so a
        // bare Keycloak login ('alice') is not a UPN: the email stands in for it.
        var upn = username is not null && username.Contains('@', StringComparison.Ordinal) ? username : mail ?? username ?? user.Id;

        return new DirectoryUser(user.Id, DisplayName(user, mail, username), upn)
        {
            Mail = mail,
            GivenName = NullIfBlank(user.FirstName),
            Surname = NullIfBlank(user.LastName),
            JobTitle = Single(user, KeycloakUserAttributes.JobTitle),
            Department = Single(user, KeycloakUserAttributes.Department),
            CompanyName = Single(user, KeycloakUserAttributes.CompanyName),
            OfficeLocation = Single(user, KeycloakUserAttributes.OfficeLocation),
            MobilePhone = Single(user, KeycloakUserAttributes.PhoneNumber),
            BusinessPhones = Many(user, KeycloakUserAttributes.BusinessPhones),
            Aliases = [.. Many(user, KeycloakUserAttributes.Aliases)
                .Where(a => !string.Equals(a, mail, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)],
        };
    }

    private static string DisplayName(KeycloakUser user, string? mail, string? username)
    {
        var full = string.Join(' ', new[] { user.FirstName, user.LastName }.Where(static n => !string.IsNullOrWhiteSpace(n)).Select(static n => n!.Trim()));
        return full.Length > 0 ? full : username ?? mail ?? user.Id;
    }

    private static string? Single(KeycloakUser user, string attribute) => NullIfBlank(user.Attribute(attribute));

    private static IReadOnlyList<string> Many(KeycloakUser user, string attribute) =>
        user.Attributes is not null && user.Attributes.TryGetValue(attribute, out var values)
            ? [.. values.Where(static v => !string.IsNullOrWhiteSpace(v)).Select(static v => v.Trim())]
            : [];

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
