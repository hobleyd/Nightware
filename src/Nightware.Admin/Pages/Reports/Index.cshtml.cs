using System.Globalization;

using Microsoft.AspNetCore.Mvc;

using Nightware.Application.Administration;
using Nightware.Application.Administration.Reports;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Pages.Reports;

/// <summary>
/// The DMARC aggregate and SMTP TLS reports other mail systems sent about the actor's domains, as the mail server
/// analysed them: a summary per domain, then each report with its sending addresses (DMARC) or its policy results
/// (TLS). Read-only; <c>?domain=</c> narrows it to one domain.
/// </summary>
public sealed class IndexModel(IAdminActor actor, ListMailReports list) : ConsolePageModel(actor)
{
    [BindProperty(SupportsGet = true)]
    public string? Domain { get; set; }

    public MailReportOverview Overview { get; private set; } = new([], null, [], [], []);

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!TryGet(await list.ExecuteAsync(Domain, cancellationToken), out MailReportOverview overview, out IActionResult? failure))
        {
            return failure!;
        }

        Overview = overview;
        return Page();
    }

    /// <summary>A report's period: one day as "24 Nov 2026", longer as "24–30 Nov 2026" or across months in full.</summary>
    public static string Period(DateTimeOffset start, DateTimeOffset end)
    {
        DateTime from = start.UtcDateTime;
        DateTime to = end.UtcDateTime;
        CultureInfo c = CultureInfo.InvariantCulture;
        if (from.Date == to.Date || (to - from) <= TimeSpan.FromDays(1) && to.TimeOfDay == TimeSpan.Zero)
        {
            return from.ToString("d MMM yyyy", c);
        }

        return from.Year == to.Year && from.Month == to.Month
            ? $"{from.Day.ToString(c)}–{to.ToString("d MMM yyyy", c)}"
            : $"{from.ToString("d MMM yyyy", c)} – {to.ToString("d MMM yyyy", c)}";
    }

    /// <summary>What the receiver did with the messages, in words.</summary>
    public static string Disposition(string disposition) => disposition switch
    {
        "none" or "pass" => "Delivered",
        "quarantine" => "Quarantined",
        "reject" => "Rejected",
        _ => "Not said",
    };

    /// <summary>A published policy's <c>p=</c> or <c>sp=</c>.</summary>
    public static string Policy(string disposition) => disposition switch
    {
        "none" => "none (monitor)",
        "quarantine" => "quarantine",
        "reject" => "reject",
        _ => "not said",
    };

    public static string Verdict(DmarcVerdict verdict) => verdict switch
    {
        DmarcVerdict.Pass => "Pass",
        DmarcVerdict.Fail => "Fail",
        _ => "—",
    };

    public static string VerdictClass(DmarcVerdict verdict) => verdict switch
    {
        DmarcVerdict.Pass => "linked",
        DmarcVerdict.Fail => "error",
        _ => "disabled",
    };

    /// <summary>The TLS policy a sender applied, in words.</summary>
    public static string TlsPolicy(string policyType) => policyType switch
    {
        "sts" => "MTA-STS",
        "tlsa" => "DANE",
        "noPolicyFound" => "None found",
        _ => "Other",
    };

    /// <summary>RFC 8460's result types, from Stalwart's camelCase names.</summary>
    public static string TlsResult(string resultType) => resultType switch
    {
        "startTlsNotSupported" => "STARTTLS not offered",
        "certificateHostMismatch" => "Certificate does not match the host",
        "certificateExpired" => "Certificate expired",
        "certificateNotTrusted" => "Certificate not trusted",
        "validationFailure" => "Certificate validation failed",
        "tlsaInvalid" => "TLSA records invalid",
        "dnssecInvalid" => "DNSSEC invalid",
        "daneRequired" => "DANE required but not usable",
        "stsPolicyFetchError" => "MTA-STS policy could not be fetched",
        "stsPolicyInvalid" => "MTA-STS policy invalid",
        "stsWebpkiInvalid" => "MTA-STS policy host's certificate invalid",
        _ => "Other failure",
    };

    public static string Count(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
}
