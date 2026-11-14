namespace Nightware.Adapters.Directory.Directory;

/// <summary>
/// Keycloak user attributes that back Graph <c>user</c> properties Keycloak has no built-in field for.
/// Keycloak 24+ keeps only attributes its realm user profile declares, so a deployment that wants these
/// filled declares them (admin-editable) the way <c>deploy/keycloak/configure.sh</c> declares the
/// mailboxSettings ones; undeclared, they are simply absent and the Graph properties are null.
/// </summary>
internal static class KeycloakUserAttributes
{
    /// <summary>Keycloak's own attribute for the OIDC <c>phone_number</c> claim (the built-in <c>phone</c> scope) → <c>mobilePhone</c>.</summary>
    public const string PhoneNumber = "phoneNumber";

    public const string JobTitle = "nightware_job_title";

    public const string Department = "nightware_department";

    public const string CompanyName = "nightware_company_name";

    public const string OfficeLocation = "nightware_office_location";

    /// <summary>Multi-valued → <c>businessPhones</c>.</summary>
    public const string BusinessPhones = "nightware_business_phones";

    /// <summary>Multi-valued secondary SMTP addresses → <c>proxyAddresses</c> <c>smtp:</c> entries.</summary>
    public const string Aliases = "nightware_aliases";
}
