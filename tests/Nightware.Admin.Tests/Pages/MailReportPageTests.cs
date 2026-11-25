using System.Net;

using Nightware.Admin.Pages.Reports;
using Nightware.Admin.Tests.Support;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Tests.Pages;

public sealed class MailReportPageTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Day = new(2026, 11, 24, 0, 0, 0, TimeSpan.Zero);

    private readonly ConsoleHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static MailReports Held() => new(
        [
            new DmarcAggregateReport("r1", "google.com", "1234", "acme.example", Day, Day.AddDays(1).AddSeconds(-1), Day.AddDays(1),
                new DmarcPublishedPolicy("reject", "reject", "strict", "strict", false),
                [
                    new DmarcSource("192.0.2.10", 12, "none", DmarcVerdict.Pass, DmarcVerdict.Pass, "acme.example", "acme.example", [new AuthenticationCheck("acme.example", "v1-rsa", "pass")], []),
                    new DmarcSource("198.51.100.7", 3, "reject", DmarcVerdict.Fail, DmarcVerdict.Fail, "acme.example", "spoof.example", [], [new AuthenticationCheck("spoof.example", "mailFrom", "softFail")]),
                ]),
            new DmarcAggregateReport("r2", "Yahoo", "y", "other.example", Day, Day.AddDays(1), null,
                new DmarcPublishedPolicy("none", "none", "relaxed", "relaxed", false), [new DmarcSource("192.0.2.99", 7, "none", DmarcVerdict.Fail, DmarcVerdict.Fail, null, null, [], [])]),
        ],
        [
            new TlsReport("t1", "Google Inc.", "t", Day, Day.AddDays(1).AddSeconds(-1), null,
            [
                new TlsPolicyResult("noPolicyFound", "acme.example", [], 3, 0, []),
                new TlsPolicyResult("sts", "acme.example", ["mail.acme.example"], 5, 2, [new TlsFailure("certificateHostMismatch", "209.85.220.41", "mail.acme.example", null, 2, null)]),
            ]),
        ]);

    [Fact]
    public async Task An_entity_admin_sees_their_domains_reports_and_why_mail_failed()
    {
        _host.Reports.ListAsync(Arg.Any<CancellationToken>()).Returns(Held());

        string html = await _host.ClientAs(ConsoleHost.EntityAdmin).GetStringAsync("/reports", Ct);

        html.ShouldContain("<h1>DMARC reports</h1>");
        html.ShouldContain("198.51.100.7 (3)");
        html.ShouldContain("Some mail claiming your domains failed DMARC");
        html.ShouldContain("2 sending addresses");
        html.ShouldContain("spoof.example");
        html.ShouldContain("None found");
        html.ShouldContain("Certificate does not match the host");
        html.ShouldContain("24 Nov 2026");
        html.ShouldNotContain("other.example");
        html.ShouldNotContain("192.0.2.99");
    }

    [Fact]
    public async Task A_global_admin_can_narrow_the_page_to_one_domain()
    {
        _host.Reports.ListAsync(Arg.Any<CancellationToken>()).Returns(Held());
        HttpClient client = _host.ClientAs(ConsoleHost.GlobalAdmin);

        string all = await client.GetStringAsync("/reports", Ct);
        all.ShouldContain("All domains");
        all.ShouldContain("192.0.2.99");

        string one = await client.GetStringAsync("/reports?domain=other.example", Ct);
        one.ShouldContain("192.0.2.99");
        one.ShouldNotContain("198.51.100.7");
    }

    [Fact]
    public async Task Asking_for_someone_elses_domain_is_forbidden()
    {
        using HttpResponseMessage response = await _host.ClientAs(ConsoleHost.EntityAdmin).GetAsync("/reports?domain=other.example", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task No_reports_yet_says_so()
    {
        string html = await _host.ClientAs(ConsoleHost.EntityAdmin).GetStringAsync("/reports", Ct);

        html.ShouldContain("No DMARC reports yet.");
        html.ShouldContain("No TLS reports yet.");
    }

    [Fact]
    public async Task A_mail_server_that_cannot_be_read_is_not_shown_as_no_reports()
    {
        _host.Reports.ListAsync(Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("Stalwart is down."));

        string html = await _host.ClientAs(ConsoleHost.EntityAdmin).GetStringAsync("/reports", Ct);

        html.ShouldContain("Stalwart is down.");
        html.ShouldNotContain("No DMARC reports yet.");
    }

    [Fact]
    public async Task Every_admin_finds_it_in_the_menu()
    {
        string html = await _host.ClientAs(ConsoleHost.EntityAdmin).GetStringAsync("/", Ct);

        html.ShouldContain("href=\"/reports\"");
    }

    [Theory]
    [InlineData("2026-11-24T00:00:00Z", "2026-11-24T23:59:59Z", "24 Nov 2026")]
    [InlineData("2026-11-24T00:00:00Z", "2026-11-25T00:00:00Z", "24 Nov 2026")]
    [InlineData("2026-11-24T00:00:00Z", "2026-11-30T23:59:59Z", "24–30 Nov 2026")]
    [InlineData("2026-11-28T00:00:00Z", "2026-12-02T00:00:00Z", "28 Nov 2026 – 2 Dec 2026")]
    public void A_period_reads_as_dates(string start, string end, string expected) =>
        IndexModel.Period(DateTimeOffset.Parse(start, System.Globalization.CultureInfo.InvariantCulture), DateTimeOffset.Parse(end, System.Globalization.CultureInfo.InvariantCulture)).ShouldBe(expected);

    [Fact]
    public void Codes_read_as_words()
    {
        IndexModel.Disposition("quarantine").ShouldBe("Quarantined");
        IndexModel.Disposition("none").ShouldBe("Delivered");
        IndexModel.Disposition("unspecified").ShouldBe("Not said");
        IndexModel.Policy("none").ShouldBe("none (monitor)");
        IndexModel.Verdict(DmarcVerdict.Unspecified).ShouldBe("—");
        IndexModel.VerdictClass(DmarcVerdict.Fail).ShouldBe("error");
        IndexModel.TlsPolicy("tlsa").ShouldBe("DANE");
        IndexModel.TlsResult("stsPolicyFetchError").ShouldBe("MTA-STS policy could not be fetched");
        IndexModel.TlsResult("somethingNew").ShouldBe("Other failure");
        IndexModel.Count(12345).ShouldBe("12,345");
    }
}
