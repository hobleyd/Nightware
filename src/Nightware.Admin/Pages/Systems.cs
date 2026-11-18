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
        DnsChangeKind.Remove => "Remove",
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

    /// <summary>A moment in a user's history, in UTC so every admin reads the same time: "18 Nov 2026, 09:12 UTC".</summary>
    public static string When(DateTimeOffset at) =>
        at.UtcDateTime.ToString("d MMM yyyy, HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>One history entry in words: "Offboarded by ops: Mail failed, Files &amp; calendar none".</summary>
    public static string HistoryLine(AccessEvent entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string what = entry.Kind switch
        {
            AccessEventKind.Offboarded => "Offboarded",
            AccessEventKind.OffboardingFinished => "Offboarding run again",
            AccessEventKind.Reinstated => "Reinstated",
            AccessEventKind.MailDeleted => "Mail deleted",
            _ => "Files & calendar data deleted",
        };
        string systems = string.Join(", ", entry.Systems.Select(static s => $"{ShortName(s.Backend)} {AfterWord(s.State)}"));
        return systems.Length == 0 ? $"{what} by {entry.By}" : $"{what} by {entry.By}: {systems}";
    }

    /// <summary>What a system's state means straight after an access change.</summary>
    private static string AfterWord(LinkState state) => state switch
    {
        LinkState.Disabled => "shut",
        LinkState.Missing => "none",
        LinkState.Error => "failed",
        LinkState.Linked => "open",
        LinkState.Pending => "pending first sign-in",
        _ => "in conflict",
    };

    /// <summary>
    /// Rows for a read-only record value: enough to show it whole even in the narrower change table (about 45
    /// characters a line), at most six; a spare line is better than a hidden part of a key.
    /// </summary>
    public static int ValueRows(string value) => Math.Clamp(((value?.Length ?? 0) / 45) + 1, 1, 6);

    /// <summary>
    /// What an SPF record means for <paramref name="domain"/>, worked out from the record itself: who it lets send,
    /// and what happens to everyone else. "Only 81.2.69.160 and the MX hosts may send mail for acme.example; other
    /// senders are refused."
    /// </summary>
    public static string SpfMeaning(SpfRecord spf, string domain)
    {
        ArgumentNullException.ThrowIfNull(spf);
        string[] terms = [.. spf.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)];
        var senders = new List<string>();
        foreach (string term in terms.Select(static t => t.TrimStart('+')))
        {
            string lower = term.ToLowerInvariant();
            if (lower.StartsWith("ip4:", StringComparison.Ordinal) || lower.StartsWith("ip6:", StringComparison.Ordinal))
            {
                senders.Add(term[4..]);
            }
            else if (lower == "mx" || lower.StartsWith("mx:", StringComparison.Ordinal) || lower.StartsWith("mx/", StringComparison.Ordinal))
            {
                senders.Add("the MX hosts");
            }
            else if (lower == "a" || lower.StartsWith("a:", StringComparison.Ordinal) || lower.StartsWith("a/", StringComparison.Ordinal))
            {
                senders.Add(lower == "a" ? $"{domain}'s own address" : $"{term[2..]}'s address");
            }
            else if (lower.StartsWith("include:", StringComparison.Ordinal))
            {
                senders.Add($"whatever {term[8..]} lists");
            }
            else if (lower.StartsWith("redirect=", StringComparison.Ordinal))
            {
                return $"{domain} uses {term[9..]}'s SPF record: whoever it lets send may send mail for {domain}.";
            }
        }

        string? all = terms.LastOrDefault(static t => t.TrimStart('+', '-', '~', '?').Equals("all", StringComparison.OrdinalIgnoreCase))?.ToLowerInvariant();
        if (all is "+all" or "all")
        {
            return $"Any server may send mail for {domain}: +all lets every sender through.";
        }

        string others = all switch
        {
            "-all" => "other senders are refused",
            "~all" => "other senders only soft-fail, and most receivers still accept their mail",
            _ => "other senders are not refused",
        };
        string[] named = [.. senders.Distinct()];
        string who = named.Length switch
        {
            0 => "No server",
            1 => $"Only {named[0]}",
            _ => $"Only {string.Join(", ", named[..^1])} and {named[^1]}",
        };
        return $"{who} may send mail for {domain}; {others}.";
    }

    /// <summary>A weakness's chip word: "Unsafe" lets anyone send as the domain, "Weak" leaves something open.</summary>
    public static string WeaknessLabel(WeaknessSeverity severity) => severity == WeaknessSeverity.Unsafe ? "Unsafe" : "Weak";

    /// <summary>A weakness's chip class: red for unsafe, amber for weak.</summary>
    public static string WeaknessClass(WeaknessSeverity severity) => severity == WeaknessSeverity.Unsafe ? "error" : "missing";

    /// <summary>
    /// The chip class for an SPF or DMARC record as a whole: green with nothing open, otherwise the colour of its
    /// worst weakness.
    /// </summary>
    public static string StrengthClass(IReadOnlyList<MailAuthenticationWeakness> weaknesses)
    {
        ArgumentNullException.ThrowIfNull(weaknesses);
        return weaknesses.Count == 0 ? "linked" : WeaknessClass(weaknesses.Max(static w => w.Severity));
    }

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
