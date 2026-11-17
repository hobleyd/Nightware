using Nightware.Domain.Administration;

namespace Nightware.Adapters.Directory.Administration;

/// <summary>
/// The names the console relies on in the realm (ADR 0002 D1, D4, D5, D10): realm roles, the user-profile and
/// organization attributes it writes, and Keycloak's required-action aliases. <c>deploy/keycloak</c> declares
/// the same names.
/// </summary>
internal static class KeycloakAdminNames
{
    /// <summary>Realm role of a global administrator.</summary>
    public const string GlobalAdminRole = "nightware-global-admin";

    /// <summary>Realm role carried by every entity's <c>admins</c> organization group (a token hint only).</summary>
    public const string EntityAdminRole = "nightware-entity-admin";

    /// <summary>Multivalued user attribute: the user's secondary addresses.</summary>
    public const string AliasesAttribute = "nightware_aliases";

    /// <summary>User attribute: when the console deleted the user's mail (ISO-8601 round-trip).</summary>
    public const string MailDeletedAtAttribute = "nightware_mail_deleted_at";

    /// <summary>User attribute: when the console deleted the user's Nextcloud data (ISO-8601 round-trip).</summary>
    public const string GroupwareDeletedAtAttribute = "nightware_groupware_deleted_at";

    /// <summary>Organization attribute <c>nightware_spf:&lt;domain&gt;</c>: the domain's chosen SPF record.</summary>
    public static string SpfAttribute(MailDomain domain) => "nightware_spf:" + domain.Name;

    /// <summary>Organization attribute <c>nightware_dmarc:&lt;domain&gt;</c>: the domain's chosen DMARC record.</summary>
    public static string DmarcAttribute(MailDomain domain) => "nightware_dmarc:" + domain.Name;

    /// <summary>
    /// Organization attribute: the entity's Cloudflare API token, sealed by the console (ASP.NET Data Protection).
    /// Never the token itself: Keycloak keeps organization representations in its admin events.
    /// </summary>
    public const string DnsTokenAttribute = "nightware_dns_token:cloudflare";

    public static string Alias(RequiredAction action) => action switch
    {
        RequiredAction.UpdatePassword => "UPDATE_PASSWORD",
        RequiredAction.ConfigureTotp => "CONFIGURE_TOTP",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown required action."),
    };

    public static string MarkerAttribute(DeletionTarget target) => target switch
    {
        DeletionTarget.Mail => MailDeletedAtAttribute,
        DeletionTarget.GroupwareData => GroupwareDeletedAtAttribute,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown deletion target."),
    };
}
