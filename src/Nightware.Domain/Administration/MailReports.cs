namespace Nightware.Domain.Administration;

/// <summary>How a receiver judged one DMARC check (DKIM or SPF) for a group of messages: aligned pass, fail, or not said.</summary>
public enum DmarcVerdict
{
    Unspecified,
    Pass,
    Fail,
}

/// <summary>One raw DKIM or SPF result a receiver saw: the domain checked, a DKIM selector or SPF scope, and the result as reported.</summary>
public sealed record AuthenticationCheck(string Domain, string? SelectorOrScope, string Result);

/// <summary>The DMARC record the receiver found when it evaluated the mail (<c>p=</c>, <c>sp=</c>, <c>adkim=</c>, <c>aspf=</c>, <c>t=</c>).</summary>
public sealed record DmarcPublishedPolicy(string Disposition, string SubdomainDisposition, string DkimAlignment, string SpfAlignment, bool Testing);

/// <summary>
/// One row of a DMARC aggregate report: messages from one sending address, with what the receiver did with them
/// (<see cref="Disposition"/>) and how DKIM and SPF evaluated.
/// </summary>
public sealed record DmarcSource(
    string? SourceIp,
    long Count,
    string Disposition,
    DmarcVerdict Dkim,
    DmarcVerdict Spf,
    string? HeaderFrom,
    string? EnvelopeFrom,
    IReadOnlyList<AuthenticationCheck> DkimResults,
    IReadOnlyList<AuthenticationCheck> SpfResults)
{
    /// <summary>DMARC passes when either aligned DKIM or aligned SPF passes.</summary>
    public bool PassesDmarc => Dkim == DmarcVerdict.Pass || Spf == DmarcVerdict.Pass;
}

/// <summary>A DMARC aggregate report (RFC 7489 §7.2) another mail system sent about mail claiming one of our domains.</summary>
public sealed record DmarcAggregateReport(
    string Id,
    string Organization,
    string ReportId,
    string Domain,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    DateTimeOffset? ReceivedAt,
    DmarcPublishedPolicy Policy,
    IReadOnlyList<DmarcSource> Sources)
{
    public long Messages => Sources.Sum(static s => s.Count);

    /// <summary>Messages that failed DMARC: neither aligned DKIM nor aligned SPF passed.</summary>
    public long Failing => Sources.Where(static s => !s.PassesDmarc).Sum(static s => s.Count);
}

/// <summary>One kind of TLS failure a sender met delivering to us (RFC 8460 §4.3), with the sessions it affected.</summary>
public sealed record TlsFailure(string ResultType, string? SendingIp, string? ReceivingMx, string? ReceivingIp, long Sessions, string? Detail);

/// <summary>
/// A TLS report's result for one policy: the policy the sender applied (<c>sts</c>, <c>tlsa</c> or
/// <c>noPolicyFound</c>), for which domain, and how many sessions succeeded or failed under it.
/// </summary>
public sealed record TlsPolicyResult(string PolicyType, string Domain, IReadOnlyList<string> MxHosts, long Successful, long Failed, IReadOnlyList<TlsFailure> Failures)
{
    /// <summary>The sender found neither an MTA-STS policy nor DANE records, so TLS to us was opportunistic.</summary>
    public bool NoPolicyFound => string.Equals(PolicyType, "noPolicyFound", StringComparison.Ordinal);
}

/// <summary>An SMTP TLS report (RFC 8460) another mail system sent about delivering to our domains.</summary>
public sealed record TlsReport(
    string Id,
    string Organization,
    string ReportId,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    DateTimeOffset? ReceivedAt,
    IReadOnlyList<TlsPolicyResult> Policies)
{
    public long Successful => Policies.Sum(static p => p.Successful);

    public long Failed => Policies.Sum(static p => p.Failed);

    /// <summary>The report narrowed to the policies for domains <paramref name="include"/> accepts, or null when none is left.</summary>
    public TlsReport? Only(Func<string, bool> include)
    {
        ArgumentNullException.ThrowIfNull(include);
        List<TlsPolicyResult> kept = [.. Policies.Where(p => include(p.Domain))];
        return kept.Count == 0 ? null : this with { Policies = kept };
    }
}

/// <summary>The reports the mail server holds: DMARC aggregate reports and TLS reports.</summary>
public sealed record MailReports(IReadOnlyList<DmarcAggregateReport> Dmarc, IReadOnlyList<TlsReport> Tls)
{
    public static MailReports None { get; } = new([], []);
}

/// <summary>A sending address that failed DMARC, with the messages reported from it.</summary>
public sealed record FailingSource(string SourceIp, long Messages);

/// <summary>What the reports say about one domain, over every report held.</summary>
/// <param name="SendersFoundNoTlsPolicy">Some report held, however old, says a sender found no MTA-STS policy.</param>
/// <param name="LatestFoundNoTlsPolicy">
/// A sender's newest TLS report for the domain says it found no MTA-STS policy: the problem is current, not one
/// fixed since. Older reports stay held for 30 days, so only this one says the domain still needs fixing.
/// </param>
public sealed record DomainReportSummary(
    string Domain,
    int DmarcReports,
    long Messages,
    long Failing,
    IReadOnlyList<FailingSource> FailingSources,
    int TlsReports,
    long TlsSuccessful,
    long TlsFailed,
    bool SendersFoundNoTlsPolicy,
    bool LatestFoundNoTlsPolicy)
{
    /// <summary>
    /// The summary of <paramref name="reports"/> for <paramref name="domain"/>: reports about it or one of its
    /// subdomains. Failing sources are listed most messages first.
    /// </summary>
    public static DomainReportSummary For(string domain, MailReports reports)
    {
        ArgumentException.ThrowIfNullOrEmpty(domain);
        ArgumentNullException.ThrowIfNull(reports);
        List<DmarcAggregateReport> dmarc = [.. reports.Dmarc.Where(r => ReportDomains.Covers(domain, r.Domain))];
        List<TlsPolicyResult> tls = [.. reports.Tls.SelectMany(static r => r.Policies).Where(p => ReportDomains.Covers(domain, p.Domain))];
        List<FailingSource> failing = [.. dmarc.SelectMany(static r => r.Sources).Where(static s => !s.PassesDmarc)
            .GroupBy(static s => s.SourceIp ?? "unknown", StringComparer.Ordinal)
            .Select(static g => new FailingSource(g.Key, g.Sum(static s => s.Count)))
            .OrderByDescending(static f => f.Messages).ThenBy(static f => f.SourceIp, StringComparer.Ordinal)];
        return new DomainReportSummary(
            domain,
            dmarc.Count,
            dmarc.Sum(static r => r.Messages),
            dmarc.Sum(static r => r.Failing),
            failing,
            reports.Tls.Count(r => r.Policies.Any(p => ReportDomains.Covers(domain, p.Domain))),
            tls.Sum(static p => p.Successful),
            tls.Sum(static p => p.Failed),
            tls.Any(static p => p.NoPolicyFound),
            NewestFindsNoTlsPolicy(domain, reports.Tls));
    }

    /// <summary>
    /// Whether any sender's newest report for <paramref name="domain"/> (by period end, then arrival) has a
    /// "no policy found" result for it. Each sending organization is judged by its own latest report.
    /// </summary>
    private static bool NewestFindsNoTlsPolicy(string domain, IReadOnlyList<TlsReport> reports) =>
        reports.Where(r => r.Policies.Any(p => ReportDomains.Covers(domain, p.Domain)))
            .GroupBy(static r => r.Organization, StringComparer.OrdinalIgnoreCase)
            .Select(static g => g.OrderByDescending(static r => r.PeriodEnd).ThenByDescending(static r => r.ReceivedAt).First())
            .Any(r => r.Policies.Any(p => p.NoPolicyFound && ReportDomains.Covers(domain, p.Domain)));
}

/// <summary>Which of our domains a report is about.</summary>
public static class ReportDomains
{
    /// <summary>
    /// A report about <paramref name="reported"/> belongs to <paramref name="domain"/> when it names the domain or
    /// one of its subdomains (a DMARC record covers its subdomains through <c>sp=</c>). Case and a trailing dot are ignored.
    /// </summary>
    public static bool Covers(string domain, string? reported)
    {
        ArgumentNullException.ThrowIfNull(domain);
        if (string.IsNullOrWhiteSpace(reported))
        {
            return false;
        }

        string ours = domain.TrimEnd('.');
        string theirs = reported.Trim().TrimEnd('.');
        return string.Equals(theirs, ours, StringComparison.OrdinalIgnoreCase)
            || theirs.EndsWith("." + ours, StringComparison.OrdinalIgnoreCase);
    }
}
