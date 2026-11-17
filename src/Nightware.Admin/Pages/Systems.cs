using Nightware.Domain.Administration;

namespace Nightware.Admin.Pages;

/// <summary>
/// One name per backend on every page: what it does first, the product second, so an entity admin can read the
/// console without knowing the stack behind it.
/// </summary>
public static class Systems
{
    /// <summary>The full name, for tables and messages: "Mail (Stalwart)".</summary>
    public static string Name(BackendKind backend) => backend switch
    {
        BackendKind.Identity => "Sign-in (Keycloak)",
        BackendKind.Mail => "Mail (Stalwart)",
        _ => "Files & calendar (Nextcloud)",
    };

    /// <summary>The short name, for chips: "Mail".</summary>
    public static string ShortName(BackendKind backend) => backend switch
    {
        BackendKind.Identity => "Sign-in",
        BackendKind.Mail => "Mail",
        _ => "Files & calendar",
    };
}
