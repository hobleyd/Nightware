using Microsoft.Extensions.DependencyInjection;

using Nightware.Admin.Tests.Support;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Tests.Pages;

/// <summary>
/// MTA-STS across the console: its records are DNS changes like any other (dashboard, DNS page); a TLS report finding
/// no policy is dated evidence, with a fix beside it on the reports page while changes wait; the mail server's policy
/// leaving out the domain's MX is a server setting, fixed from the dashboard or the DNS page.
/// </summary>
public sealed class MtaStsAttentionPageTests : IDisposable
{
    private const string Token = "cf-SECRET-token-0123456789abcdefghijklmn";
    private static readonly DateTimeOffset Day = new(2026, 11, 24, 0, 0, 0, TimeSpan.Zero);

    private static readonly DnsRecord[] Needed =
    [
        new("acme.example", "MX", 3600, "10 mail.acme.example"),
        new("mta-sts.acme.example", "CNAME", 3600, "mail.acme.example"),
        new("_mta-sts.acme.example", "TXT", 3600, "v=STSv1; id=1"),
    ];

    /// <summary>Everything the DNS page would publish: the mail server's records and the default SPF and DMARC.</summary>
    private static readonly DnsRecord[] AllPublished =
    [
        .. Needed,
        new("acme.example", "TXT", 3600, "v=spf1 mx -all"),
        new("_dmarc.acme.example", "TXT", 3600, "v=DMARC1; p=reject; sp=reject; adkim=s; aspf=s; rua=mailto:postmaster@acme.example"),
    ];

    private readonly ConsoleHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string DnsUrl => $"/entities/{ConsoleHost.Acme}/dns/acme.example";

    public MtaStsAttentionPageTests()
    {
        _host.MailDomains.GetDnsRecordsAsync(default!, Ct).ReturnsForAnyArgs(Result<IReadOnlyList<DnsRecord>>.Success(Needed));
        _host.Entities.ListMembersAsync(ConsoleHost.Acme, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<IdentityAccount>>.Success([]));
    }

    public void Dispose() => _host.Dispose();

    private static TlsReport Report(int day, string type) =>
        new("t" + day, "Google Inc.", "t", Day.AddDays(day), Day.AddDays(day + 1), null, [new TlsPolicyResult(type, "acme.example", [], 3, 0, [])]);

    private void Reported(params TlsReport[] tls) => _host.Reports.ListAsync(Arg.Any<CancellationToken>()).Returns(new MailReports([], tls));

    /// <summary>Acme connected to Cloudflare, whose zone holds <paramref name="zone"/>.</summary>
    private void Connected(params DnsRecord[] zone)
    {
        _host.Entities.GetAsync(ConsoleHost.Acme, Arg.Any<CancellationToken>()).Returns(ConsoleHost.AcmeEntity with { DnsHost = DnsProvider.Cloudflare });
        string sealedToken = _host.Services.GetRequiredService<ISecretSealer>().Seal("dns-token:cloudflare:" + ConsoleHost.Acme, Token);
        _host.Entities.GetSealedDnsTokenAsync(default!, Ct).ReturnsForAnyArgs(Result<SealedDnsToken?>.Success(new SealedDnsToken(DnsProvider.Cloudflare, sealedToken)));
        _host.DnsHosting.FindZoneAsync(default!, default!, Ct).ReturnsForAnyArgs(new DnsZone("z-acme", "acme.example"));
        _host.DnsHosting.ListRecordsAsync(default!, default!, Ct).ReturnsForAnyArgs(Result<IReadOnlyList<ZoneRecord>>.Success([.. zone.Select((r, i) => new ZoneRecord($"r-{i}", r))]));
    }

    private async Task<string> GetAsync(string url, string sub = ConsoleHost.GlobalAdmin) =>
        await (await _host.ClientAs(sub).GetAsync(new Uri(url, UriKind.Relative), Ct)).Content.ReadAsStringAsync(Ct);

    [Fact]
    public async Task Unpublished_MTA_STS_records_are_DNS_changes_like_any_other()
    {
        Connected([.. AllPublished.Where(r => !r.Name.Contains("mta-sts", StringComparison.Ordinal))]);
        Reported(Report(0, "noPolicyFound"));

        string dashboard = await GetAsync("/");
        dashboard.ShouldContain("2 DNS records are missing or out of date at Cloudflare.");
        dashboard.ShouldContain($"href=\"{DnsUrl}#publish\">Review and publish</a>");
        dashboard.ShouldNotContain("MTA-STS");
        dashboard.ShouldNotContain("TLS reports</a>");

        string dns = await GetAsync(DnsUrl);
        dns.ShouldContain("<section class=\"panel\" id=\"publish\">");
        dns.ShouldContain("<td data-label=\"Name\">_mta-sts.acme.example</td>");
        dns.ShouldContain("<td data-label=\"Name\">mta-sts.acme.example</td>");
        dns.ShouldNotContain("id=\"mta-sts\"");
    }

    [Fact]
    public async Task A_TLS_report_finding_no_policy_alone_puts_nothing_on_the_dashboard_or_the_DNS_page()
    {
        Connected(AllPublished);
        Reported(Report(0, "noPolicyFound"));

        (await GetAsync("/")).ShouldContain("Nothing needs you.");
        string dns = await GetAsync(DnsUrl);
        dns.ShouldNotContain("class=\"warning\"");
        dns.ShouldContain("has every record acme.example needs.");
    }

    [Fact]
    public async Task The_reports_page_puts_a_fix_beside_a_current_none_found_while_DNS_changes_wait()
    {
        Connected(Needed[0]);
        // The same sender, a day apart.
        Reported(Report(1, "noPolicyFound"), Report(0, "noPolicyFound"));

        string html = await GetAsync("/reports");

        html.ShouldContain($"<a href=\"{DnsUrl}#publish\">acme.example</a>");
        html.ShouldContain($"<a class=\"fix\" href=\"{DnsUrl}#publish\" title=\"Fix: publish ");
        html.ShouldContain(" DNS changes for acme.example\" aria-label=\"Fix: publish ");
        // Only the sender's newest report carries it; the older one is history.
        CountOf(html, "class=\"fix\"").ShouldBe(1);
        html.ShouldContain("acme.example has DNS changes to publish. The fix icon beside \"None found\" opens them.");
        html.ShouldNotContain("Older reports keep saying so");
    }

    [Fact]
    public async Task With_every_record_published_the_reports_page_points_at_the_policy_host_instead()
    {
        Connected(AllPublished);
        Reported(Report(0, "noPolicyFound"));

        string html = await GetAsync("/reports");

        html.ShouldNotContain("class=\"fix\"");
        html.ShouldContain($"<a href=\"{DnsUrl}\">acme.example</a>");
        html.ShouldContain("No DNS changes are waiting for acme.example at a connected DNS host.");
        html.ShouldContain("https://mta-sts.&lt;domain&gt;/.well-known/mta-sts.txt opens");
    }

    [Fact]
    public async Task An_entity_admin_is_told_to_ask_the_operator_and_older_reports_are_history()
    {
        Reported(Report(0, "noPolicyFound"));

        string html = await GetAsync("/reports", ConsoleHost.EntityAdmin);

        html.ShouldContain("<section class=\"panel reports\">");
        html.ShouldNotContain("class=\"fix\"");
        html.ShouldContain("Ask your Nightware operator to publish acme.example's DNS records.");
        int table = html.IndexOf("aria-label=\"TLS reports\"", StringComparison.Ordinal);
        html.IndexOf("id=\"no-tls-policy\"", StringComparison.Ordinal).ShouldBeGreaterThan(table);

        Reported(Report(0, "noPolicyFound"), Report(1, "sts"));
        string fixedSince = await GetAsync("/reports", ConsoleHost.EntityAdmin);
        fixedSince.ShouldContain("Older reports keep saying so after it is fixed");
        fixedSince.ShouldNotContain("Ask your Nightware operator");
    }

    private static int CountOf(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public async Task A_policy_that_leaves_out_the_domains_MX_is_on_the_dashboard_and_fixed_from_the_DNS_page()
    {
        Connected(AllPublished);
        _host.MtaSts.GetAsync(Arg.Any<CancellationToken>()).Returns(new MtaStsPolicy("testing", [], ["mail.server.example"]));

        string dashboard = await GetAsync("/");
        dashboard.ShouldContain("The mail server's MTA-STS policy does not list its MX, mail.acme.example.");
        dashboard.ShouldContain("Add to the policy</button>");

        string dns = await GetAsync(DnsUrl);
        dns.ShouldContain("The mail server's MTA-STS policy does not list mail.acme.example</strong>");
        dns.ShouldContain("It lists mail.server.example.");
        dns.ShouldContain("report each delivery to the domain as a failure");
        dns.ShouldContain("Add mail.acme.example to the policy</button>");

        using HttpResponseMessage posted = await ConsoleHost.PostFormAsync(_host.ClientAs(ConsoleHost.GlobalAdmin), DnsUrl, $"{DnsUrl}?handler=MtaStsHosts", new Dictionary<string, string>());

        posted.StatusCode.ShouldBe(System.Net.HttpStatusCode.Redirect);
        await _host.MtaSts.Received(1).SetMxHostsAsync(Arg.Is<IReadOnlyList<string>>(h => h.SequenceEqual(new[] { "mail.server.example", "mail.acme.example" })), Arg.Any<CancellationToken>());

        // The dashboard's button does the same.
        using HttpResponseMessage fromDashboard = await ConsoleHost.PostFormAsync(_host.ClientAs(ConsoleHost.GlobalAdmin), "/", "/?handler=MtaStsHosts", new Dictionary<string, string> { ["entityId"] = ConsoleHost.Acme, ["domain"] = "acme.example" });
        fromDashboard.StatusCode.ShouldBe(System.Net.HttpStatusCode.Redirect);
        await _host.MtaSts.Received(2).SetMxHostsAsync(Arg.Is<IReadOnlyList<string>>(h => h.SequenceEqual(new[] { "mail.server.example", "mail.acme.example" })), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_entity_admin_sees_the_missing_MX_but_no_button()
    {
        _host.MtaSts.GetAsync(Arg.Any<CancellationToken>()).Returns(new MtaStsPolicy("enforce", [], ["mail.server.example"]));

        string dns = await GetAsync(DnsUrl, ConsoleHost.EntityAdmin);

        dns.ShouldContain("refuse to deliver the domain&#x27;s mail");
        dns.ShouldContain("Ask your Nightware operator to add the domain's MX to the mail server's MTA-STS policy.");
        dns.ShouldNotContain("handler=MtaStsHosts");
        (await GetAsync("/", ConsoleHost.EntityAdmin)).ShouldNotContain("MTA-STS policy does not list");
    }
}
