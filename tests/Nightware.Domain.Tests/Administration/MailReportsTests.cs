using Nightware.Domain.Administration;

namespace Nightware.Domain.Tests.Administration;

public sealed class MailReportsTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 24, 0, 0, 0, TimeSpan.Zero);

    private static DmarcSource Source(string ip, long count, DmarcVerdict dkim, DmarcVerdict spf) =>
        new(ip, count, "none", dkim, spf, "acme.example", "acme.example", [], []);

    private static DmarcAggregateReport Dmarc(string domain, params DmarcSource[] sources) =>
        new("r-" + domain, "google.com", "1", domain, Day, Day.AddDays(1).AddSeconds(-1), Day.AddDays(1),
            new DmarcPublishedPolicy("reject", "reject", "strict", "strict", false), sources);

    private static TlsPolicyResult Policy(string domain, string type, long ok, long failed) => new(type, domain, [], ok, failed, []);

    private static TlsReport Tls(params TlsPolicyResult[] policies) => new("t1", "Google Inc.", "t", Day, Day.AddDays(1), null, policies);

    [Fact]
    public void A_source_passes_DMARC_when_either_aligned_check_passes()
    {
        Source("1.1.1.1", 1, DmarcVerdict.Pass, DmarcVerdict.Fail).PassesDmarc.ShouldBeTrue();
        Source("1.1.1.1", 1, DmarcVerdict.Fail, DmarcVerdict.Pass).PassesDmarc.ShouldBeTrue();
        Source("1.1.1.1", 1, DmarcVerdict.Fail, DmarcVerdict.Fail).PassesDmarc.ShouldBeFalse();
        Source("1.1.1.1", 1, DmarcVerdict.Unspecified, DmarcVerdict.Unspecified).PassesDmarc.ShouldBeFalse();
    }

    [Fact]
    public void A_DMARC_report_counts_its_messages_and_the_failing_ones()
    {
        DmarcAggregateReport report = Dmarc("acme.example",
            Source("192.0.2.10", 12, DmarcVerdict.Pass, DmarcVerdict.Pass),
            Source("198.51.100.7", 3, DmarcVerdict.Fail, DmarcVerdict.Fail));

        report.Messages.ShouldBe(15);
        report.Failing.ShouldBe(3);
    }

    [Fact]
    public void A_TLS_report_totals_its_sessions_and_narrows_to_some_domains()
    {
        TlsReport report = Tls(Policy("acme.example", "sts", 5, 2), Policy("other.example", "noPolicyFound", 3, 0));

        report.Successful.ShouldBe(8);
        report.Failed.ShouldBe(2);
        report.Only(d => d == "acme.example")!.Policies.ShouldHaveSingleItem().Domain.ShouldBe("acme.example");
        report.Only(_ => false).ShouldBeNull();
        report.Policies[1].NoPolicyFound.ShouldBeTrue();
        report.Policies[0].NoPolicyFound.ShouldBeFalse();
    }

    [Theory]
    [InlineData("acme.example", true)]
    [InlineData("ACME.example.", true)]
    [InlineData("mail.acme.example", true)]
    [InlineData("notacme.example", false)]
    [InlineData("acme.example.org", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_report_covers_the_domain_and_its_subdomains(string? reported, bool covered) =>
        ReportDomains.Covers("acme.example", reported).ShouldBe(covered);

    [Fact]
    public void The_summary_adds_up_one_domains_reports_and_lists_failing_senders_most_first()
    {
        var reports = new MailReports(
            [
                Dmarc("acme.example", Source("192.0.2.10", 12, DmarcVerdict.Pass, DmarcVerdict.Pass), Source("198.51.100.7", 3, DmarcVerdict.Fail, DmarcVerdict.Fail)),
                Dmarc("acme.example", Source("203.0.113.9", 5, DmarcVerdict.Fail, DmarcVerdict.Unspecified), Source("198.51.100.7", 1, DmarcVerdict.Fail, DmarcVerdict.Fail)),
                Dmarc("other.example", Source("192.0.2.1", 50, DmarcVerdict.Fail, DmarcVerdict.Fail)),
            ],
            [Tls(Policy("acme.example", "noPolicyFound", 3, 0), Policy("other.example", "sts", 9, 9)), Tls(Policy("acme.example", "sts", 4, 1))]);

        DomainReportSummary summary = DomainReportSummary.For("acme.example", reports);

        summary.DmarcReports.ShouldBe(2);
        summary.Messages.ShouldBe(21);
        summary.Failing.ShouldBe(9);
        summary.FailingSources.ShouldBe([new FailingSource("203.0.113.9", 5), new FailingSource("198.51.100.7", 4)]);
        summary.TlsReports.ShouldBe(2);
        summary.TlsSuccessful.ShouldBe(7);
        summary.TlsFailed.ShouldBe(1);
        summary.SendersFoundNoTlsPolicy.ShouldBeTrue();
    }

    [Fact]
    public void A_domain_without_reports_has_an_empty_summary()
    {
        DomainReportSummary summary = DomainReportSummary.For("acme.example", MailReports.None);

        summary.DmarcReports.ShouldBe(0);
        summary.Messages.ShouldBe(0);
        summary.FailingSources.ShouldBeEmpty();
        summary.TlsReports.ShouldBe(0);
        summary.SendersFoundNoTlsPolicy.ShouldBeFalse();
    }

    [Fact]
    public void Only_a_senders_newest_TLS_report_says_whether_the_domain_still_has_no_policy()
    {
        TlsReport Report(string organization, int day, string type) =>
            new("t-" + organization + day, organization, "t", Day.AddDays(day), Day.AddDays(day + 1), null, [Policy("acme.example", type, 3, 0)]);

        DomainReportSummary Summary(params TlsReport[] tls) => DomainReportSummary.For("acme.example", new MailReports([], tls));

        // Fixed since: Google's later report found the policy, though the older one is still held.
        DomainReportSummary fixedSince = Summary(Report("Google Inc.", 0, "noPolicyFound"), Report("Google Inc.", 1, "sts"));
        fixedSince.SendersFoundNoTlsPolicy.ShouldBeTrue();
        fixedSince.LatestFoundNoTlsPolicy.ShouldBeFalse();
        fixedSince.CurrentNoTlsPolicyReports.ShouldBeEmpty();
        fixedSince.FixedNoTlsPolicyReports.ShouldBe(["t-Google Inc.0"]);

        // Still missing for Microsoft, whose newest report says so, whatever Google's says; that report alone is current.
        DomainReportSummary stillMissing = Summary(Report("Google Inc.", 1, "sts"), Report("Microsoft", 1, "noPolicyFound"), Report("microsoft", 0, "noPolicyFound"));
        stillMissing.LatestFoundNoTlsPolicy.ShouldBeTrue();
        stillMissing.CurrentNoTlsPolicyReports.ShouldBe(["t-Microsoft1"]);
        // Microsoft's older report is not fixed: its newest still finds no policy.
        stillMissing.FixedNoTlsPolicyReports.ShouldBeEmpty();

        // Another domain's missing policy is not this one's.
        DomainReportSummary.For("acme.example", new MailReports([], [Tls(Policy("other.example", "noPolicyFound", 1, 0), Policy("acme.example", "sts", 1, 0))]))
            .LatestFoundNoTlsPolicy.ShouldBeFalse();
    }
}
