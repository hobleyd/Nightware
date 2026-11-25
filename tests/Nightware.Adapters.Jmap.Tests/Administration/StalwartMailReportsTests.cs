using Nightware.Adapters.Jmap.Administration;
using Nightware.Adapters.Jmap.Tests.Support;
using Nightware.Application.Administration.Reports;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Tests.Administration;

public sealed class StalwartMailReportsTests : IAsyncDisposable
{
    private readonly StalwartManagementTestHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IMailReports Reports => _host.Get<IMailReports>();

    /// <summary>
    /// Recorded from Stalwart v1.1.0 after mailing postmaster@ a DMARC aggregate report (two sources, one failing) and
    /// Google's TLS report for acme.example plus an MTA-STS policy result with a failure.
    /// </summary>
    private static string Recorded => Fixtures.Read(Path.Combine("Administration", "stalwart-external-reports.json"));

    [Fact]
    public async Task Reads_recorded_DMARC_and_TLS_reports()
    {
        _host.Stub("x:DmarcExternalReport/query", Recorded);

        MailReports reports = (await Reports.ListAsync(Ct)).Value;

        DmarcAggregateReport dmarc = reports.Dmarc.ShouldHaveSingleItem();
        dmarc.Id.ShouldBe("jqji0rgcaaqa");
        dmarc.Organization.ShouldBe("google.com");
        dmarc.ReportId.ShouldBe("1234567890123456789");
        dmarc.Domain.ShouldBe("acme.example");
        dmarc.PeriodStart.ShouldBe(new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.Zero));
        dmarc.PeriodEnd.ShouldBe(new DateTimeOffset(2026, 11, 24, 23, 59, 59, TimeSpan.Zero));
        dmarc.ReceivedAt.ShouldBe(new DateTimeOffset(2026, 11, 25, 22, 7, 10, TimeSpan.Zero));
        dmarc.Policy.ShouldBe(new DmarcPublishedPolicy("reject", "reject", "strict", "strict", false));
        dmarc.Messages.ShouldBe(15);
        dmarc.Failing.ShouldBe(3);
        dmarc.Sources.Count.ShouldBe(2);
        DmarcSource good = dmarc.Sources[0];
        good.SourceIp.ShouldBe("192.0.2.10");
        good.Dkim.ShouldBe(DmarcVerdict.Pass);
        good.DkimResults.ShouldBe([new AuthenticationCheck("acme.example", "v1-rsa-20260101", "pass")]);
        good.SpfResults.ShouldBe([new AuthenticationCheck("acme.example", "mailFrom", "pass")]);
        DmarcSource bad = dmarc.Sources[1];
        bad.Disposition.ShouldBe("reject");
        bad.Spf.ShouldBe(DmarcVerdict.Fail);
        bad.EnvelopeFrom.ShouldBe("spoof.example");
        bad.DkimResults.ShouldBeEmpty();
        bad.SpfResults.ShouldHaveSingleItem().Result.ShouldBe("softFail");

        TlsReport tls = reports.Tls.ShouldHaveSingleItem();
        tls.Organization.ShouldBe("Google Inc.");
        tls.ReportId.ShouldBe("2026-11-24T00:00:00Z_acme.example");
        tls.Policies.Count.ShouldBe(2);
        tls.Policies[0].ShouldSatisfyAllConditions(
            p => p.NoPolicyFound.ShouldBeTrue(),
            p => p.Domain.ShouldBe("acme.example"),
            p => p.Successful.ShouldBe(3),
            p => p.Failed.ShouldBe(0));
        TlsPolicyResult sts = tls.Policies[1];
        sts.PolicyType.ShouldBe("sts");
        sts.MxHosts.ShouldBe(["mail.acme.example"]);
        sts.Failures.ShouldBe([new TlsFailure("certificateHostMismatch", "209.85.220.41", "mail.acme.example", "192.0.2.10", 2, null)]);
    }

    [Fact]
    public async Task Asks_for_both_kinds_in_one_request_with_a_limit()
    {
        _host.Stub("x:DmarcExternalReport/query", Recorded);

        await Reports.ListAsync(Ct);

        _host.Bodies().Count.ShouldBe(1);
        _host.Calls().Select(c => c[0]!.GetValue<string>()).ShouldBe(["x:DmarcExternalReport/query", "x:DmarcExternalReport/get", "x:TlsExternalReport/query", "x:TlsExternalReport/get"]);
        _host.Calls("x:DmarcExternalReport/query").Single()[1]!["limit"]!.GetValue<int>().ShouldBe(StalwartMailReports.Limit);
        _host.Calls("x:TlsExternalReport/get").Single()[1]!["#ids"]!["resultOf"]!.GetValue<string>().ShouldBe("c2");
    }

    [Fact]
    public async Task No_reports_is_an_empty_list()
    {
        _host.Stub("x:DmarcExternalReport/query", """
            [["x:DmarcExternalReport/query",{"ids":[],"position":0},"c0"],["x:DmarcExternalReport/get",{"list":[],"notFound":[]},"c1"],
             ["x:TlsExternalReport/query",{"ids":[],"position":0},"c2"],["x:TlsExternalReport/get",{"list":[],"notFound":[]},"c3"]]
            """);

        MailReports reports = (await Reports.ListAsync(Ct)).Value;

        reports.Dmarc.ShouldBeEmpty();
        reports.Tls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_missing_permission_is_the_querys_own_error()
    {
        _host.Stub("x:DmarcExternalReport/query", """
            [["error",{"type":"forbidden","description":"sysDmarcExternalReportQuery"},"c0"],["error",{"type":"invalidResultReference"},"c1"],
             ["x:TlsExternalReport/query",{"ids":[],"position":0},"c2"],["x:TlsExternalReport/get",{"list":[],"notFound":[]},"c3"]]
            """);

        Result<MailReports> result = await Reports.ListAsync(Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task A_report_without_its_period_is_skipped_and_logged()
    {
        _host.Stub("x:DmarcExternalReport/query", """
            [["x:DmarcExternalReport/query",{"ids":["a","b"],"position":0},"c0"],
             ["x:DmarcExternalReport/get",{"list":[{"id":"a","report":{"orgName":"x"}},{"id":"b","report":{"orgName":"Yahoo","policyDomain":"acme.example","dateRangeStart":"2026-11-24T00:00:00Z","dateRangeEnd":"2026-11-25T00:00:00Z","records":[{"sourceIp":"192.0.2.1","count":2,"evaluatedDkim":"pass","evaluatedSpf":"fail","evaluatedDisposition":"none"}]}}],"notFound":[]},"c1"],
             ["x:TlsExternalReport/query",{"ids":["t"],"position":0},"c2"],["x:TlsExternalReport/get",{"list":[{"id":"t","report":null}],"notFound":[]},"c3"]]
            """);

        MailReports reports = (await Reports.ListAsync(Ct)).Value;

        DmarcAggregateReport kept = reports.Dmarc.ShouldHaveSingleItem();
        kept.Organization.ShouldBe("Yahoo");
        kept.ReportId.ShouldBeEmpty();
        kept.Policy.Disposition.ShouldBe("unspecified");
        kept.Sources.ShouldHaveSingleItem().PassesDmarc.ShouldBeTrue();
        reports.Tls.ShouldBeEmpty();
        _host.Logs.Lines.Count(l => l.Contains("unreadable", StringComparison.Ordinal)).ShouldBe(2);
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();
}
