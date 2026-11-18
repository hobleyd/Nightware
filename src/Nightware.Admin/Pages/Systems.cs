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

    /// <summary>
    /// A link state in words, not the enum's. "Failed" claims only that the system gave no usable answer for this
    /// account (down, or refusing it); "Unreachable" is kept for the health check, which tests the system itself.
    /// </summary>
    public static string StateLabel(LinkState state) => state switch
    {
        LinkState.Pending => "Pending first sign-in",
        LinkState.Error => "Failed",
        _ => state.ToString(),
    };

    /// <summary>A planned change to a Cloudflare zone, as the DNS page lists it.</summary>
    public static string DnsChangeLabel(DnsChangeKind kind) => kind switch
    {
        DnsChangeKind.Create => "Add",
        DnsChangeKind.Update => "Correct",
        DnsChangeKind.Remove => "Remove duplicate",
        DnsChangeKind.Skip => "Not published",
        _ => "Published",
    };

    /// <summary>
    /// A failed link in a plain sentence. The reason the system gave (often a host and port) follows it as a hint,
    /// for whoever runs the servers; the sentence alone has to make sense to an entity admin.
    /// </summary>
    public static string FailedSentence(BackendKind backend) => $"{ShortName(backend)} gave no answer for this account.";

    /// <summary>What an entity admin does about a failure only the operator can fix.</summary>
    public const string TellOperator = "If this persists, tell your Nightware operator.";

    /// <summary>
    /// For an entity admin facing an offboarding a failed system left incomplete: who can fix it, and that the
    /// button will finish the job once that is done, so the button is not pressed in a loop.
    /// </summary>
    public static string WaitForOperator(BackendKind backend) =>
        $"{ShortName(backend)} is not answering. Tell your Nightware operator; once it answers, Finish offboarding shuts it.";

    /// <summary>
    /// A user's link in words. For an offboarded user a missing account is no problem (nothing there can let them
    /// in), so it reads "None", muted, instead of the amber "Missing" that asks for a repair.
    /// </summary>
    public static string LinkLabel(BackendLink link, bool offboarded) =>
        IsHarmlessAbsence(link, offboarded) ? "None" : StateLabel(link.State);

    /// <summary>The chip class for <see cref="LinkLabel"/>.</summary>
    public static string LinkClass(BackendLink link, bool offboarded) =>
        IsHarmlessAbsence(link, offboarded) ? "disabled" : link.State.ToString().ToLowerInvariant();

    private static bool IsHarmlessAbsence(BackendLink link, bool offboarded)
    {
        ArgumentNullException.ThrowIfNull(link);
        return offboarded && link.State == LinkState.Missing;
    }

    /// <summary>What a domain Repair achieved, the same on every page.</summary>
    public static string DomainRepairNotice(DomainStatus status) => status switch
    {
        { Mail.State: not LinkState.Linked } => $"{status.Domain} is still not served by the mail server ({status.Mail.Detail}).",
        { DkimPresent: false } => $"{status.Domain} is served by the mail server, but still has no DKIM keys.",
        _ => $"{status.Domain} is served by the mail server, with DKIM keys.",
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
