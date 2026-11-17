using Nightware.Application.Administration.Users;
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

    /// <summary>A link state in words: "Unreachable", not the enum's "Error".</summary>
    public static string StateLabel(LinkState state) => state switch
    {
        LinkState.Pending => "Pending first sign-in",
        LinkState.Error => "Unreachable",
        _ => state.ToString(),
    };

    /// <summary>A user's one-line status, the same on every page.</summary>
    public static string StatusLabel(UserStatus status) => status switch
    {
        UserStatus.Active => "Active",
        UserStatus.NeedsRepair => "Needs repair",
        UserStatus.Conflict => "Conflict",
        UserStatus.OffboardingIncomplete => "Offboarding incomplete",
        _ => "Offboarded",
    };

    /// <summary>The chip class that colours a status by what the admin should feel: fine, act, or stop.</summary>
    public static string StatusClass(UserStatus status) => status switch
    {
        UserStatus.Active => "linked",
        UserStatus.NeedsRepair => "pending",
        UserStatus.Offboarded => "disabled",
        _ => "error",
    };
}
